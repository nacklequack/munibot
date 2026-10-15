using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Munibot;

internal sealed class MunibotHostedService(
    BotConfig config,
    IMunibotSessionLifecycle session,
    ISessionClaimCoordinator claims,
    SessionProcessIdentity processIdentity,
    ILogger<MunibotHostedService> logger,
    IHostApplicationLifetime lifetime,
    TimeProvider timeProvider) : BackgroundService
{
    private static readonly TimeSpan StableConnectionDuration = TimeSpan.FromMinutes(10);
    private readonly SemaphoreSlim _closeLock = new(1, 1);
    private SessionClaimToken? _claim;
    private bool _claimClosed;
    private volatile bool _terminalAmbiguous;
    private volatile bool _draining;

    public bool IsReady => !_draining && !_claimClosed && _claim is not null && session.IsOnline;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SessionClaimAcquisition acquisition;
        try
        {
            acquisition = await claims.AcquireAsync(processIdentity.Nonce, stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogCritical(ex, "Munibot session claim could not be acquired; provider login is blocked");
            lifetime.StopApplication();
            return;
        }

        if (acquisition.Token is null)
        {
            logger.LogWarning(
                "Munibot session claim is not released; provider login is blocked; claimPhase={ClaimPhase}",
                acquisition.ExistingPhase);
            lifetime.StopApplication();
            return;
        }

        _claim = acquisition.Token;
        try
        {
            await session.LoginAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Munibot login outcome is ambiguous; retaining the session claim");
            _terminalAmbiguous = true;
            await RetainAmbiguousClaimAsync();
            lifetime.StopApplication();
            return;
        }

        if (config.Runtime.ExitAfterLoginSeconds > 0 && session.IsOnline)
        {
            logger.LogInformation(
                "Smoke-test mode enabled; staying logged in for {Seconds} second(s)",
                config.Runtime.ExitAfterLoginSeconds);

            await Task.Delay(TimeSpan.FromSeconds(config.Runtime.ExitAfterLoginSeconds), timeProvider, stoppingToken);
            lifetime.StopApplication();
            return;
        }

        logger.LogInformation("Munibot session claim is owned and the provider session is online.");

        var nextKeepaliveAt = NextKeepaliveAt();
        var stableConnectionLogged = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), timeProvider, stoppingToken);
            if (_draining)
            {
                return;
            }

            if (!session.IsOnline)
            {
                logger.LogError("Munibot disconnected without an acknowledged logout; retaining the session claim");
                _terminalAmbiguous = true;
                await RetainAmbiguousClaimAsync();
                lifetime.StopApplication();
                return;
            }

            if (!stableConnectionLogged &&
                session.CaptureTransportSnapshot() is { } transport &&
                transport.ConnectedDuration >= StableConnectionDuration)
            {
                logger.LogInformation(
                    "Simulator connection stable; connectionGeneration={ConnectionGeneration} connectedMilliseconds={ConnectedMilliseconds} lastReceivedPacketAgeMilliseconds={LastReceivedPacketAgeMilliseconds} receivedPackets={ReceivedPackets} sentPackets={SentPackets} receivedResends={ReceivedResends} resentPackets={ResentPackets}",
                    transport.ConnectionGeneration,
                    transport.ConnectedDuration.TotalMilliseconds,
                    transport.LastReceivedPacketAge?.TotalMilliseconds,
                    transport.ReceivedPackets,
                    transport.SentPackets,
                    transport.ReceivedResends,
                    transport.ResentPackets);
                stableConnectionLogged = true;
            }

            if (config.Runtime.MovementKeepaliveSeconds > 0 && timeProvider.GetUtcNow() >= nextKeepaliveAt)
            {
                try
                {
                    session.SendMovementKeepalive();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Movement keepalive failed");
                }

                nextKeepaliveAt = NextKeepaliveAt();
            }
        }
    }

    private DateTimeOffset NextKeepaliveAt()
        => timeProvider.GetUtcNow().AddSeconds(Math.Max(config.Runtime.MovementKeepaliveSeconds, 1));

    private async Task CloseClaimAsync(
        ProviderSessionCloseOutcome outcome,
        CancellationToken cancellationToken)
    {
        await _closeLock.WaitAsync(cancellationToken);
        try
        {
            if (_claim is null || _claimClosed)
            {
                return;
            }

            await claims.CloseAsync(_claim, outcome, cancellationToken);
            _claimClosed = true;
        }
        finally
        {
            _closeLock.Release();
        }
    }

    private async Task RetainAmbiguousClaimAsync()
    {
        try
        {
            await CloseClaimAsync(ProviderSessionCloseOutcome.Ambiguous, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Munibot session claim could not be marked uncertain; its existing ownership is retained");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _draining = true;
        await base.StopAsync(cancellationToken);

        if (_claim is null || _claimClosed)
        {
            return;
        }

        ProviderSessionCloseOutcome outcome;
        try
        {
            outcome = await session.LogoutAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Munibot logout outcome is ambiguous; retaining the session claim");
            outcome = ProviderSessionCloseOutcome.Ambiguous;
        }

        if (_terminalAmbiguous)
        {
            outcome = ProviderSessionCloseOutcome.Ambiguous;
        }

        try
        {
            await CloseClaimAsync(outcome, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogCritical(ex, "Munibot session claim could not be closed; its existing ownership is retained");
        }
    }
}

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Munibot;

internal sealed class MunibotHostedService(
    BotConfig config,
    IMunibotSessionLifecycle session,
    ILogger<MunibotHostedService> logger,
    IHostApplicationLifetime lifetime,
    TimeProvider timeProvider) : BackgroundService
{
    private static readonly TimeSpan StableConnectionDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RegionShutdownCooldown = TimeSpan.FromMinutes(1);
    private volatile bool _draining;

    public bool IsReady => !_draining && session.IsOnline;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await session.LoginAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Munibot login failed; restarting with a fresh process");
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

        logger.LogInformation("Munibot is online.");

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
                // Connected is cleared immediately before LibreMetaverse raises
                // Disconnected. Allow that terminal evidence to reach the adapter.
                if (session.LastDisconnect is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), timeProvider, stoppingToken);
                }

                _draining = true;
                if (session.LastDisconnect?.Kind is SessionDisconnectKind.RegionShutdown)
                {
                    logger.LogWarning(
                        "The provider ended the session for region shutdown; restarting after {CooldownSeconds} seconds",
                        RegionShutdownCooldown.TotalSeconds);
                    await Task.Delay(RegionShutdownCooldown, timeProvider, stoppingToken);
                }
                else
                {
                    logger.LogError(
                        "Munibot disconnected; restarting with a fresh process; disconnectReason={DisconnectReason}",
                        session.LastDisconnect?.Kind);
                }

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

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _draining = true;
        await base.StopAsync(cancellationToken);

        try
        {
            await session.LogoutAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Munibot logout failed");
        }
    }
}

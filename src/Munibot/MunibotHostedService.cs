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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var attempts = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                attempts++;
                await session.LoginAsync(stoppingToken);
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Munibot login failed on attempt {Attempt}", attempts);

                if (!config.Runtime.Reconnect ||
                    (config.Runtime.MaxReconnectAttempts > 0 && attempts >= config.Runtime.MaxReconnectAttempts))
                {
                    lifetime.StopApplication();
                    return;
                }

                logger.LogInformation(
                    "Retrying Munibot login in {DelaySeconds} second(s)",
                    config.Runtime.ReconnectDelaySeconds);

                await Task.Delay(TimeSpan.FromSeconds(config.Runtime.ReconnectDelaySeconds), timeProvider, stoppingToken);
            }
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

        logger.LogInformation("Munibot is online. Press Ctrl+C to logout.");

        var nextKeepaliveAt = NextKeepaliveAt();
        var stableConnectionLogged = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), timeProvider, stoppingToken);

            if (session.IsOnline)
            {
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

                if (config.Runtime.MovementKeepaliveSeconds > 0 &&
                    timeProvider.GetUtcNow() >= nextKeepaliveAt)
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

                continue;
            }

            if (!config.Runtime.Reconnect)
            {
                continue;
            }

            logger.LogWarning(
                "Munibot is offline; attempting reconnect in {DelaySeconds} second(s)",
                config.Runtime.ReconnectDelaySeconds);

            await Task.Delay(TimeSpan.FromSeconds(config.Runtime.ReconnectDelaySeconds), timeProvider, stoppingToken);

            try
            {
                await session.LoginAsync(stoppingToken);
                nextKeepaliveAt = NextKeepaliveAt();
                stableConnectionLogged = false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Munibot reconnect failed");
            }
        }
    }

    private DateTimeOffset NextKeepaliveAt()
        => timeProvider.GetUtcNow().AddSeconds(Math.Max(config.Runtime.MovementKeepaliveSeconds, 1));

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        session.Logout();
        return base.StopAsync(cancellationToken);
    }
}

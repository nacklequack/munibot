using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Munibot;

public sealed class MunibotHostedService(
    BotConfig config,
    SecondLifeBotSession session,
    ILogger<MunibotHostedService> logger,
    IHostApplicationLifetime lifetime) : BackgroundService
{
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

                await Task.Delay(TimeSpan.FromSeconds(config.Runtime.ReconnectDelaySeconds), stoppingToken);
            }
        }

        if (config.Runtime.ExitAfterLoginSeconds > 0 && session.IsOnline)
        {
            logger.LogInformation(
                "Smoke-test mode enabled; staying logged in for {Seconds} second(s)",
                config.Runtime.ExitAfterLoginSeconds);

            await Task.Delay(TimeSpan.FromSeconds(config.Runtime.ExitAfterLoginSeconds), stoppingToken);
            lifetime.StopApplication();
            return;
        }

        logger.LogInformation("Munibot is online. Press Ctrl+C to logout.");

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

            if (session.IsOnline || !config.Runtime.Reconnect)
            {
                continue;
            }

            logger.LogWarning(
                "Munibot is offline; attempting reconnect in {DelaySeconds} second(s)",
                config.Runtime.ReconnectDelaySeconds);

            await Task.Delay(TimeSpan.FromSeconds(config.Runtime.ReconnectDelaySeconds), stoppingToken);

            try
            {
                await session.LoginAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Munibot reconnect failed");
            }
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        session.Logout();
        return base.StopAsync(cancellationToken);
    }
}

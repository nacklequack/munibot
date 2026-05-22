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
        try
        {
            await session.LoginAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Munibot login failed");
            lifetime.StopApplication();
            return;
        }

        if (config.Runtime.ExitAfterLoginSeconds > 0)
        {
            logger.LogInformation(
                "Smoke-test mode enabled; staying logged in for {Seconds} second(s)",
                config.Runtime.ExitAfterLoginSeconds);

            await Task.Delay(TimeSpan.FromSeconds(config.Runtime.ExitAfterLoginSeconds), stoppingToken);
            lifetime.StopApplication();
            return;
        }

        logger.LogInformation("Munibot is online. Press Ctrl+C to logout.");
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        session.Logout();
        return base.StopAsync(cancellationToken);
    }
}

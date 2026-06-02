using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace Munibot;

public interface IWalletEventPublisher
{
    Task<WalletEventDeliveryResultDto> PublishAsync(
        WalletEventDto walletEvent,
        CancellationToken cancellationToken = default);
}

public sealed class DisabledWalletEventPublisher : IWalletEventPublisher
{
    public Task<WalletEventDeliveryResultDto> PublishAsync(
        WalletEventDto walletEvent,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new WalletEventDeliveryResultDto(false, false, 0, "disabled"));
}

public sealed class MunibaseWalletEventPublisher(
    HttpClient httpClient,
    BotConfig config,
    ILogger<MunibaseWalletEventPublisher> logger) : IWalletEventPublisher
{
    public async Task<WalletEventDeliveryResultDto> PublishAsync(
        WalletEventDto walletEvent,
        CancellationToken cancellationToken = default)
    {
        var walletEvents = config.Munibase.WalletEvents;
        if (!walletEvents.IsConfigured)
        {
            return new WalletEventDeliveryResultDto(false, false, 0, "disabled");
        }

        var attempts = Math.Max(walletEvents.MaxDeliveryAttempts, 1);
        Exception? lastException = null;
        string? lastStatus = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(walletEvents.TimeoutSeconds));

                using var content = new FormUrlEncodedContent(ToCorradeCompatibleForm(walletEvent, walletEvents.SharedSecret!));
                content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");

                using var response = await httpClient.PostAsync(walletEvents.EndpointUrl, content, timeoutCts.Token);
                lastStatus = $"HTTP {(int)response.StatusCode}";
                if (response.IsSuccessStatusCode)
                {
                    logger.LogInformation(
                        "Delivered wallet event to Munibase transaction={TransactionId} amount={Amount} source={SourceAvatarUuid} target={TargetAvatarUuid} attempt={Attempt}",
                        walletEvent.TransactionId,
                        walletEvent.Amount,
                        walletEvent.SourceAvatarUuid,
                        walletEvent.TargetAvatarUuid,
                        attempt);

                    return new WalletEventDeliveryResultDto(true, true, attempt, lastStatus);
                }

                logger.LogWarning(
                    "Munibase wallet event delivery failed transaction={TransactionId} status={Status} attempt={Attempt}/{Attempts}",
                    walletEvent.TransactionId,
                    lastStatus,
                    attempt,
                    attempts);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastException = ex;
                lastStatus = ex.Message;
                logger.LogWarning(
                    ex,
                    "Munibase wallet event delivery threw transaction={TransactionId} attempt={Attempt}/{Attempts}",
                    walletEvent.TransactionId,
                    attempt,
                    attempts);
            }

            if (attempt < attempts && walletEvents.RetryDelaySeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(walletEvents.RetryDelaySeconds), cancellationToken);
            }
        }

        return new WalletEventDeliveryResultDto(true, false, attempts, lastException?.Message ?? lastStatus);
    }

    private static IReadOnlyDictionary<string, string> ToCorradeCompatibleForm(
        WalletEventDto walletEvent,
        string sharedSecret)
        => new Dictionary<string, string>
        {
            ["shared_secret"] = sharedSecret,
            ["type"] = "economy",
            ["success"] = walletEvent.Success.ToString().ToLowerInvariant(),
            ["balance"] = walletEvent.Balance?.ToString() ?? string.Empty,
            ["amount"] = walletEvent.Amount?.ToString() ?? string.Empty,
            ["id"] = walletEvent.TransactionId,
            ["source"] = walletEvent.SourceAvatarUuid ?? string.Empty,
            ["target"] = walletEvent.TargetAvatarUuid ?? string.Empty,
            ["transaction"] = walletEvent.TransactionType ?? string.Empty,
            ["description"] = walletEvent.Description ?? string.Empty
        };
}

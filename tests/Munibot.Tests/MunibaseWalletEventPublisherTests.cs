using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Munibot;

namespace Munibot.Tests;

public sealed class MunibaseWalletEventPublisherTests
{
    [Fact]
    public async Task PublishAsync_PostsCorradeCompatibleFormFields()
    {
        string? body = null;
        var publisher = new MunibaseWalletEventPublisher(
            new HttpClient(new RecordingHandler(async request =>
            {
                body = await request.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.OK);
            })),
            new BotConfig
            {
                Munibase = new BotMunibaseConfig
                {
                    WalletEvents = new BotMunibaseWalletEventsConfig
                    {
                        EndpointUrl = "https://example.com/webhooks/second-life-money",
                        SharedSecret = "callback-secret"
                    }
                }
            },
            NullLogger<MunibaseWalletEventPublisher>.Instance);

        var result = await publisher.PublishAsync(new WalletEventDto(
            true,
            1234,
            50,
            "txn-1",
            "11111111-1111-1111-1111-111111111111",
            "00000000-0000-0000-0000-000000000001",
            "Payment",
            "Rental payment",
            DateTimeOffset.UtcNow));

        Assert.True(result.Enabled);
        Assert.True(result.Delivered);
        Assert.Equal(1, result.Attempts);
        Assert.NotNull(body);
        Assert.Contains("shared_secret=callback-secret", body);
        Assert.Contains("type=economy", body);
        Assert.Contains("success=true", body);
        Assert.Contains("balance=1234", body);
        Assert.Contains("amount=50", body);
        Assert.Contains("id=txn-1", body);
        Assert.Contains("source=11111111-1111-1111-1111-111111111111", body);
        Assert.Contains("target=00000000-0000-0000-0000-000000000001", body);
        Assert.Contains("transaction=Payment", body);
        Assert.Contains("description=Rental+payment", body);
    }

    [Fact]
    public async Task PublishAsync_WhenUnconfigured_ReturnsDisabledWithoutPosting()
    {
        var called = false;
        var publisher = new MunibaseWalletEventPublisher(
            new HttpClient(new RecordingHandler(_ =>
            {
                called = true;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            })),
            new BotConfig(),
            NullLogger<MunibaseWalletEventPublisher>.Instance);

        var result = await publisher.PublishAsync(new WalletEventDto(
            true,
            1234,
            50,
            "txn-1",
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow));

        Assert.False(result.Enabled);
        Assert.False(result.Delivered);
        Assert.False(called);
    }

    [Fact]
    public async Task PublishAsync_RetriesTransientFailures()
    {
        var calls = 0;
        var publisher = new MunibaseWalletEventPublisher(
            new HttpClient(new RecordingHandler(_ =>
            {
                calls++;
                return Task.FromResult(new HttpResponseMessage(
                    calls == 1 ? HttpStatusCode.BadGateway : HttpStatusCode.OK));
            })),
            new BotConfig
            {
                Munibase = new BotMunibaseConfig
                {
                    WalletEvents = new BotMunibaseWalletEventsConfig
                    {
                        EndpointUrl = "https://example.com/webhooks/second-life-money",
                        SharedSecret = "callback-secret",
                        MaxDeliveryAttempts = 2,
                        RetryDelaySeconds = 0
                    }
                }
            },
            NullLogger<MunibaseWalletEventPublisher>.Instance);

        var result = await publisher.PublishAsync(new WalletEventDto(
            true,
            1234,
            50,
            "txn-1",
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow));

        Assert.True(result.Enabled);
        Assert.True(result.Delivered);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, calls);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => handler(request);
    }
}

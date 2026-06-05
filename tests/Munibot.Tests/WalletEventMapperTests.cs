using Munibot;
using OpenMetaverse;

namespace Munibot.Tests;

public sealed class WalletEventMapperTests
{
    [Fact]
    public void FromTransactionDetails_MapsCorradeCompatibleEconomyEvent()
    {
        var botId = UUID.Parse("00000000-0000-0000-0000-000000000001");
        var payerId = "11111111-1111-1111-1111-111111111111";
        var occurredAt = new DateTimeOffset(2026, 5, 25, 3, 12, 0, TimeSpan.Zero);

        var result = WalletEventMapper.FromTransactionDetails(
            true,
            1234,
            "txn-1",
            new FakeTransactionInfo
            {
                Amount = -50,
                SourceID = payerId,
                DestID = botId.ToString(),
                TransactionType = "Payment"
            },
            "Rental payment",
            botId,
            occurredAt);

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Equal(1234, result.Balance);
        Assert.Equal(50, result.Amount);
        Assert.Equal("txn-1", result.TransactionId);
        Assert.Equal(payerId, result.SourceAvatarUuid);
        Assert.Equal(botId.ToString(), result.TargetAvatarUuid);
        Assert.Equal("Payment", result.TransactionType);
        Assert.Equal("Rental payment", result.Description);
        Assert.Equal(occurredAt, result.OccurredAtUtc);
    }

    [Fact]
    public void FromTransactionDetails_ReturnsNullWhenTransactionDetailsAreIncomplete()
    {
        var result = WalletEventMapper.FromTransactionDetails(
            true,
            1234,
            "txn-1",
            new { SourceID = "11111111-1111-1111-1111-111111111111" },
            "missing amount",
            UUID.Parse("00000000-0000-0000-0000-000000000001"),
            DateTimeOffset.UtcNow);

        Assert.Null(result);
    }

    [Fact]
    public void FromAccountHistoryTransaction_MapsIncomingPayment()
    {
        var botId = UUID.Parse("00000000-0000-0000-0000-000000000001");
        var payerId = "11111111-1111-1111-1111-111111111111";
        var occurredAt = new DateTimeOffset(2026, 5, 26, 2, 44, 0, TimeSpan.Zero);

        var result = WalletEventMapper.FromAccountHistoryTransaction(
            new AccountHistoryTransactionDto(
                "22222222-2222-2222-2222-222222222222",
                "Payment",
                "Rental payment",
                "Payment Sender",
                occurredAt,
                12115,
                10),
            payerId,
            botId,
            observedDelta: 10);

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Equal(12115, result.Balance);
        Assert.Equal(10, result.Amount);
        Assert.Equal("22222222-2222-2222-2222-222222222222", result.TransactionId);
        Assert.Equal(payerId, result.SourceAvatarUuid);
        Assert.Equal(botId.ToString(), result.TargetAvatarUuid);
        Assert.Equal("Payment", result.TransactionType);
        Assert.Equal("Rental payment", result.Description);
        Assert.Equal(occurredAt, result.OccurredAtUtc);
    }

    [Fact]
    public void FromAccountHistoryTransaction_UsesObservedDeltaWhenHistoryDeltaIsMissing()
    {
        var result = WalletEventMapper.FromAccountHistoryTransaction(
            new AccountHistoryTransactionDto(
                "22222222-2222-2222-2222-222222222222",
                "Payment",
                null,
                "Payment Sender",
                DateTimeOffset.UtcNow,
                12115,
                null),
            "11111111-1111-1111-1111-111111111111",
            UUID.Parse("00000000-0000-0000-0000-000000000001"),
            observedDelta: 10);

        Assert.NotNull(result);
        Assert.Equal(10, result.Amount);
    }

    [Fact]
    public void FromAccountHistoryTransaction_UsesHistoryDeltaWhenObservedDeltaIsMissing()
    {
        var result = WalletEventMapper.FromAccountHistoryTransaction(
            new AccountHistoryTransactionDto(
                "22222222-2222-2222-2222-222222222222",
                "Payment",
                "Test Rental",
                "Payment Sender",
                DateTimeOffset.UtcNow,
                12115,
                160),
            "11111111-1111-1111-1111-111111111111",
            UUID.Parse("00000000-0000-0000-0000-000000000001"),
            observedDelta: null);

        Assert.NotNull(result);
        Assert.Equal(160, result.Amount);
    }

    private sealed class FakeTransactionInfo
    {
        public int Amount { get; init; }
        public string SourceID { get; init; } = string.Empty;
        public string DestID { get; init; } = string.Empty;
        public string TransactionType { get; init; } = string.Empty;
    }
}

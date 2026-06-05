using Munibot;

namespace Munibot.Tests;

public sealed class WalletHistoryReconciliationTests
{
    [Fact]
    public void SelectIncomingCandidates_MatchesExactTransactionWhenBalanceDeltaIsUnavailable()
    {
        var fromUtc = new DateTimeOffset(2026, 6, 5, 10, 20, 0, TimeSpan.Zero);
        var toUtc = new DateTimeOffset(2026, 6, 5, 10, 40, 0, TimeSpan.Zero);
        var targetTransactionId = "33333333-3333-3333-3333-333333333333";

        var transactions = new[]
        {
            new AccountHistoryTransactionDto(
                "11111111-1111-1111-1111-111111111111",
                "Payment",
                "Different payment",
                "Other Resident",
                new DateTimeOffset(2026, 6, 5, 10, 28, 0, TimeSpan.Zero),
                10750,
                160),
            new AccountHistoryTransactionDto(
                targetTransactionId,
                "Payment",
                "Test Rental",
                "Example Resident",
                new DateTimeOffset(2026, 6, 5, 10, 29, 31, TimeSpan.Zero),
                10910,
                160)
        };

        var result = WalletHistoryReconciliation.SelectIncomingCandidates(
            transactions,
            fromUtc,
            toUtc,
            currentBalance: 10910,
            transactionId: targetTransactionId);

        var candidate = Assert.Single(result);
        Assert.Equal(targetTransactionId, candidate.TransactionId);
    }

    [Fact]
    public void SelectIncomingCandidates_RejectsOutgoingExactTransaction()
    {
        var transactionId = "33333333-3333-3333-3333-333333333333";
        var transactions = new[]
        {
            new AccountHistoryTransactionDto(
                transactionId,
                "Payment",
                "Refund",
                "Other Resident",
                new DateTimeOffset(2026, 6, 5, 10, 29, 31, TimeSpan.Zero),
                10910,
                -160)
        };

        var result = WalletHistoryReconciliation.SelectIncomingCandidates(
            transactions,
            new DateTimeOffset(2026, 6, 5, 10, 20, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 5, 10, 40, 0, TimeSpan.Zero),
            currentBalance: 10910,
            transactionId: transactionId);

        Assert.Empty(result);
    }
}

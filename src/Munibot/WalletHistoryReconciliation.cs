namespace Munibot;

public static class WalletHistoryReconciliation
{
    public static IReadOnlyList<AccountHistoryTransactionDto> SelectIncomingCandidates(
        IEnumerable<AccountHistoryTransactionDto> transactions,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int currentBalance,
        string? transactionId = null)
    {
        var normalizedTransactionId = NormalizeTransactionId(transactionId);

        return transactions
            .Where(transaction => transaction.OccurredAtUtc >= fromUtc && transaction.OccurredAtUtc <= toUtc)
            .Where(transaction =>
                normalizedTransactionId is null ||
                string.Equals(transaction.TransactionId, normalizedTransactionId, StringComparison.OrdinalIgnoreCase))
            .Where(transaction => IsPotentialIncomingWalletTransaction(transaction, currentBalance))
            .OrderBy(transaction => transaction.OccurredAtUtc)
            .ThenBy(transaction => transaction.TransactionId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsPotentialIncomingWalletTransaction(
        AccountHistoryTransactionDto transaction,
        int currentBalance)
    {
        if (string.IsNullOrWhiteSpace(transaction.Resident))
        {
            return false;
        }

        if (transaction.InferredAmountDelta.HasValue)
        {
            return transaction.InferredAmountDelta.Value > 0;
        }

        return transaction.EndBalance == unchecked((uint)currentBalance);
    }

    private static string? NormalizeTransactionId(string? transactionId)
        => string.IsNullOrWhiteSpace(transactionId) ? null : transactionId.Trim();
}

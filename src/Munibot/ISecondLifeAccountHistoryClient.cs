namespace Munibot;

public interface ISecondLifeAccountHistoryClient
{
    Task<AccountHistoryResponseDto> GetTransactionsAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default);
}

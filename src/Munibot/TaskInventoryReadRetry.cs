namespace Munibot;

/// <summary>Retries an unanswered read; never use this around an inventory mutation.</summary>
public sealed class TaskInventoryReadRetry(TimeSpan? attemptTimeout = null)
{
    private readonly TimeSpan timeout = attemptTimeout ?? TimeSpan.FromSeconds(15);

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> read, string code, string message, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout);
            try
            {
                var result = await read(deadline.Token);
                // Some SDK helpers swallow cancellation and return an empty response.
                deadline.Token.ThrowIfCancellationRequested();
                return result;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                if (attempt == 2) throw new TaskInventoryException(code, message, true);
            }
        }
        throw new InvalidOperationException("The inventory read retry limit was exceeded.");
    }
}

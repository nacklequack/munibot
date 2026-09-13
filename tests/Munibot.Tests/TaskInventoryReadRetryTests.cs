namespace Munibot.Tests;

public sealed class TaskInventoryReadRetryTests
{
    private readonly TaskInventoryReadRetry retry = new(TimeSpan.FromMilliseconds(10));

    [Fact]
    public async Task LostReadReplyIsRetriedBeforeTheOperationExpires()
    {
        var attempts = 0;
        var value = await retry.RunAsync(async ct =>
        {
            if (++attempts == 1) await Task.Delay(Timeout.Infinite, ct);
            return "verified source";
        }, "source_read_timeout", "Source read timed out.", default);
        Assert.Equal("verified source", value);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ExhaustionReportsTheFailedReadStageWithABoundedRequestCount()
    {
        var attempts = 0;
        var error = await Assert.ThrowsAsync<TaskInventoryException>(() => retry.RunAsync(async ct =>
        {
            attempts++;
            await Task.Delay(Timeout.Infinite, ct);
            return false;
        }, "script_state_timeout", "Script running-state read timed out.", default));
        Assert.Equal(3, attempts);
        Assert.Equal("script_state_timeout", error.Code);
        Assert.True(error.Retryable);
        Assert.False(error.OutcomeUnknown);
    }

    [Fact]
    public async Task SdkSwallowedCancellationNeverAuthorizesAnEmptyInventory()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<TaskInventoryException>(() => retry.RunAsync(async ct =>
        {
            attempts++;
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { }
            return Array.Empty<string>();
        }, "inventory_list_timeout", "Inventory list timed out.", default));
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task CallerCancellationStopsWithoutRetrying()
    {
        using var caller = new CancellationTokenSource();
        var attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retry.RunAsync(async ct =>
        {
            attempts++;
            await caller.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return true;
        }, "source_read_timeout", "Read timed out.", caller.Token));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task PermissionFailureIsReturnedImmediately()
    {
        var denied = new TaskInventoryException("source_permission_denied", "Permission denied.");
        var attempts = 0;
        var error = await Assert.ThrowsAsync<TaskInventoryException>(() => retry.RunAsync<bool>(_ =>
        {
            attempts++;
            throw denied;
        }, "source_read_timeout", "Read timed out.", default));
        Assert.Same(denied, error);
        Assert.Equal(1, attempts);
    }
}

using OpenMetaverse;

namespace Munibot.Tests;

public sealed class ActiveGroupControllerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-uuid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task InvalidGroupDoesNotReachSimulator(string? id)
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Controller.SetActiveGroupAsync(new(id), default));
        Assert.Equal(0, fixture.Grid.Reads);
        Assert.Empty(fixture.Grid.Activations);
    }

    [Fact]
    public async Task OfflineFailsWithoutReportingStaleGroup()
    {
        using var fixture = new Fixture();
        fixture.Grid.SessionId = null;
        var error = await Assert.ThrowsAsync<ActiveGroupException>(() => fixture.Controller.GetActiveGroupAsync(default));
        Assert.Equal("bot_offline", error.Code);
        Assert.Equal(0, fixture.Grid.Reads);
    }

    [Fact]
    public async Task AlreadyActiveIsConfirmedByQueryWithoutAnotherActivation()
    {
        using var fixture = new Fixture();
        var result = await fixture.Controller.SetActiveGroupAsync(new(fixture.Grid.State.GroupId), default);
        Assert.True(result.Success);
        Assert.False(result.Changed);
        Assert.Equal(1, fixture.Grid.Reads);
        Assert.Equal(0, fixture.Grid.MembershipQueries);
        Assert.Empty(fixture.Grid.Activations);
    }

    [Fact]
    public async Task NonMemberIsRejectedBeforeSendingActivation()
    {
        using var fixture = new Fixture();
        fixture.Grid.Member = false;
        var error = await Assert.ThrowsAsync<ActiveGroupException>(() => fixture.ChangeAsync());
        Assert.Equal("not_group_member", error.Code);
        Assert.False(error.OutcomeUnknown);
        Assert.Empty(fixture.Grid.Activations);
    }

    [Fact]
    public async Task QueryTimeoutDoesNotSendActivationOrHoldInventory()
    {
        using var fixture = new Fixture();
        fixture.Grid.Read = _ => throw new OperationCanceledException();
        var error = await Assert.ThrowsAsync<ActiveGroupException>(() => fixture.ChangeAsync());
        Assert.Equal("group_query_timeout", error.Code);
        Assert.False(error.OutcomeUnknown);
        Assert.Empty(fixture.Grid.Activations);
        await fixture.AssertInventoryReadyAsync();
    }

    [Fact]
    public async Task ActivationWaitsForConfirmationHoldingBothLocksEvenIfCallerCancels()
    {
        using var fixture = new Fixture();
        using var cancelledCaller = new CancellationTokenSource();
        var completion = new TaskCompletionSource<BotActiveGroupDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Grid.Activate = (_, ct) => completion.Task.WaitAsync(ct);
        var change = fixture.Controller.SetActiveGroupAsync(new(fixture.Next.ToString()), cancelledCaller.Token);
        await fixture.Grid.Sent.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancelledCaller.Cancel();

        Assert.False(change.IsCompleted);
        Assert.False(await fixture.Teleport.WaitAsync(0));
        Assert.False(await fixture.Inventory.WaitAsync(0));
        completion.SetResult(fixture.Grid.State with { GroupId = fixture.Next.ToString() });
        var result = await change;
        Assert.True(result.Success);
        Assert.True(result.Changed);
        Assert.Equal(fixture.Next.ToString(), result.ActiveGroup.GroupId);
        Assert.Equal(fixture.Grid.State.GroupId, result.PreviousGroupId);
        Assert.Equal(1, fixture.Teleport.CurrentCount);
        Assert.Equal(1, fixture.Inventory.CurrentCount);
    }

    [Fact]
    public async Task ExistingInventoryOperationFinishesBeforeGroupQueryOrMutation()
    {
        using var fixture = new Fixture();
        await fixture.Inventory.WaitAsync();
        var change = fixture.ChangeAsync();
        Assert.Equal(0, fixture.Grid.Reads);
        Assert.False(change.IsCompleted);
        fixture.Inventory.Release();
        Assert.True((await change).Success);
    }

    [Fact]
    public async Task CancelWhileWaitingDoesNotReleaseAnotherOperationsLocks()
    {
        using var fixture = new Fixture();
        await fixture.Teleport.WaitAsync();
        using var cancellation = new CancellationTokenSource();
        var change = fixture.Controller.SetActiveGroupAsync(new(fixture.Next.ToString()), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => change);
        Assert.Equal(0, fixture.Teleport.CurrentCount);
        Assert.Equal(1, fixture.Inventory.CurrentCount);
        Assert.Empty(fixture.Grid.Activations);
        fixture.Teleport.Release();
    }

    [Fact]
    public async Task ConcurrentSetRequestsAreSerializedAndDuplicateDoesNotResend()
    {
        using var fixture = new Fixture();
        var completion = new TaskCompletionSource<BotActiveGroupDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Grid.Activate = async (_, ct) => fixture.Grid.State = await completion.Task.WaitAsync(ct);
        var first = fixture.ChangeAsync();
        await fixture.Grid.Sent.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = fixture.ChangeAsync();
        Assert.False(second.IsCompleted);
        Assert.Single(fixture.Grid.Activations);
        completion.SetResult(fixture.Grid.State with { GroupId = fixture.Next.ToString() });
        Assert.True((await first).Changed);
        Assert.False((await second).Changed);
        Assert.Single(fixture.Grid.Activations);
    }

    [Fact]
    public async Task TimedOutActivationHoldsInventoryAndRejectsAnotherGroupUntilReadbackMatches()
    {
        using var fixture = new Fixture();
        fixture.Grid.Activate = (_, _) => throw new OperationCanceledException();
        var error = await Assert.ThrowsAsync<ActiveGroupException>(() => fixture.ChangeAsync());
        Assert.Equal("group_change_timeout", error.Code);
        Assert.True(error.OutcomeUnknown);

        var oldState = await fixture.Controller.GetActiveGroupAsync(default);
        Assert.Equal(fixture.Next.ToString(), oldState.PendingGroupId);
        Assert.NotEqual(oldState.GroupId, oldState.PendingGroupId);
        await fixture.AssertInventoryHeldAsync();
        var second = await Assert.ThrowsAsync<ActiveGroupException>(() =>
            fixture.Controller.SetActiveGroupAsync(new(UUID.Random().ToString()), default));
        Assert.Equal("group_change_unconfirmed", second.Code);
        Assert.Single(fixture.Grid.Activations);

        fixture.Grid.State = fixture.Grid.State with { GroupId = fixture.Next.ToString() };
        await fixture.AssertInventoryReadyAsync();
        Assert.Null((await fixture.Controller.GetActiveGroupAsync(default)).PendingGroupId);
        Assert.False((await fixture.ChangeAsync()).Changed);
        Assert.Single(fixture.Grid.Activations);
    }

    [Fact]
    public async Task ReconnectInvalidatesUnconfirmedRequestFromOldSession()
    {
        using var fixture = new Fixture();
        fixture.Grid.Activate = (_, _) => throw new OperationCanceledException();
        await Assert.ThrowsAsync<ActiveGroupException>(() => fixture.ChangeAsync());
        fixture.Grid.SessionId = UUID.Random();
        await fixture.AssertInventoryReadyAsync();
        Assert.Null((await fixture.Controller.GetActiveGroupAsync(default)).PendingGroupId);
    }

    [Fact]
    public async Task DisconnectAfterSendReturnsUncertainOutcome()
    {
        using var fixture = new Fixture();
        fixture.Grid.Activate = (_, _) => throw new ActiveGroupException("bot_offline", "Connection lost.");
        var error = await Assert.ThrowsAsync<ActiveGroupException>(() => fixture.ChangeAsync());
        Assert.Equal("bot_offline", error.Code);
        Assert.True(error.OutcomeUnknown);
        await fixture.AssertInventoryHeldAsync();
    }

    [Fact]
    public async Task ReplyFromDifferentLoginCannotConfirmActivation()
    {
        using var fixture = new Fixture();
        fixture.Grid.Activate = (group, _) =>
        {
            fixture.Grid.SessionId = UUID.Random();
            return Task.FromResult(fixture.Grid.State with { GroupId = group.ToString() });
        };
        var error = await Assert.ThrowsAsync<ActiveGroupException>(() => fixture.ChangeAsync());
        Assert.Equal("group_change_unconfirmed", error.Code);
        Assert.True(error.OutcomeUnknown);
    }

    private sealed class Fixture : IDisposable
    {
        public FakeActiveGroupTransport Grid { get; } = new();
        public SemaphoreSlim Teleport { get; } = new(1, 1);
        public SemaphoreSlim Inventory { get; } = new(1, 1);
        public UUID Next { get; } = UUID.Random();
        public ActiveGroupController Controller { get; }
        public Fixture() => Controller = new(Grid, Teleport, Inventory, TimeSpan.FromSeconds(5));
        public Task<SetActiveGroupResultDto> ChangeAsync() => Controller.SetActiveGroupAsync(new(Next.ToString()), default);
        public async Task AssertInventoryReadyAsync()
        {
            await Inventory.WaitAsync();
            try { await Controller.EnsureSettledForInventoryAsync(default); }
            finally { Inventory.Release(); }
        }
        public async Task AssertInventoryHeldAsync()
        {
            var error = await Assert.ThrowsAsync<ActiveGroupException>(AssertInventoryReadyAsync);
            Assert.Equal("group_change_unconfirmed", error.Code);
        }
        public void Dispose() { Teleport.Dispose(); Inventory.Dispose(); }
    }
}

internal sealed class FakeActiveGroupTransport : IActiveGroupTransport
{
    public UUID? SessionId { get; set; } = UUID.Random();
    public BotActiveGroupDto State { get; set; } = new(UUID.Random().ToString(), "Example Group", "Member", "None", DateTimeOffset.UtcNow);
    public bool Member { get; set; } = true;
    public int Reads { get; private set; }
    public int MembershipQueries { get; private set; }
    public List<UUID> Activations { get; } = [];
    public TaskCompletionSource Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Func<CancellationToken, Task<BotActiveGroupDto>>? Read { get; set; }
    public Func<UUID, CancellationToken, Task<BotActiveGroupDto>>? Activate { get; set; }
    public Task<BotActiveGroupDto> ReadAsync(CancellationToken ct)
    {
        Reads++;
        return Read?.Invoke(ct) ?? Task.FromResult(State);
    }
    public Task<bool> IsMemberAsync(UUID groupId, CancellationToken ct)
    {
        MembershipQueries++;
        return Task.FromResult(Member);
    }
    public Task<BotActiveGroupDto> ActivateAsync(UUID groupId, CancellationToken ct)
    {
        Activations.Add(groupId);
        Sent.TrySetResult();
        return Activate?.Invoke(groupId, ct) ?? Task.FromResult(State = State with { GroupId = groupId.ToString() });
    }
}

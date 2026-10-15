using System.Collections.Concurrent;

namespace Munibot.Tests;

public sealed class SessionClaimCoordinatorTests
{
    [Fact]
    public async Task ConcurrentAcquirersAllowExactlyOneProviderSession()
    {
        var store = new InMemoryClaimStore(SessionClaimState.Released("1", generation: 7));
        var first = new SessionClaimCoordinator(store);
        var second = new SessionClaimCoordinator(store);

        var results = await Task.WhenAll(
            first.AcquireAsync("process-a", CancellationToken.None),
            second.AcquireAsync("process-b", CancellationToken.None));

        var acquired = Assert.Single(results, result => result.Token is not null);
        Assert.Equal(8, acquired.Token!.Generation);
        Assert.Single(results, result => result.Token is null);
        Assert.Equal(SessionClaimPhase.Owned, store.State.Phase);
    }

    [Fact]
    public async Task UncertainClaimHasNoTimeBasedTakeover()
    {
        var store = new InMemoryClaimStore(new SessionClaimState(
            "9",
            SessionClaimPhase.Uncertain,
            "previous-process",
            12));
        var coordinator = new SessionClaimCoordinator(store);

        var result = await coordinator.AcquireAsync("new-process", CancellationToken.None);

        Assert.Null(result.Token);
        Assert.Equal(SessionClaimPhase.Uncertain, store.State.Phase);
        Assert.Equal("previous-process", store.State.HolderIdentity);
        Assert.Equal(12, store.State.Generation);
    }

    [Fact]
    public async Task AcknowledgedReleaseClearsHolderAndNextGenerationIsMonotonic()
    {
        var store = new InMemoryClaimStore(SessionClaimState.Released("1", generation: 4));
        var coordinator = new SessionClaimCoordinator(store);
        var token = Assert.IsType<SessionClaimToken>(
            (await coordinator.AcquireAsync("process-a", CancellationToken.None)).Token);

        await coordinator.CloseAsync(token, ProviderSessionCloseOutcome.Acknowledged, CancellationToken.None);
        var next = await coordinator.AcquireAsync("process-b", CancellationToken.None);

        Assert.Equal(SessionClaimPhase.Owned, store.State.Phase);
        Assert.Equal("process-b", store.State.HolderIdentity);
        Assert.Equal(6, next.Token!.Generation);
    }

    [Fact]
    public async Task AmbiguousCloseRetainsHolderAndGeneration()
    {
        var store = new InMemoryClaimStore(SessionClaimState.Released("1", generation: 2));
        var coordinator = new SessionClaimCoordinator(store);
        var token = (await coordinator.AcquireAsync("process-a", CancellationToken.None)).Token!;

        await coordinator.CloseAsync(token, ProviderSessionCloseOutcome.Ambiguous, CancellationToken.None);

        Assert.Equal(SessionClaimPhase.Uncertain, store.State.Phase);
        Assert.Equal("process-a", store.State.HolderIdentity);
        Assert.Equal(3, store.State.Generation);
        Assert.Null((await coordinator.AcquireAsync("process-b", CancellationToken.None)).Token);
    }

    [Fact]
    public async Task StaleTokenCannotReleaseANewerGeneration()
    {
        var store = new InMemoryClaimStore(new SessionClaimState(
            "10",
            SessionClaimPhase.Owned,
            "process-b",
            9));
        var coordinator = new SessionClaimCoordinator(store);

        await coordinator.CloseAsync(
            new SessionClaimToken("process-a", Generation: 8),
            ProviderSessionCloseOutcome.Acknowledged,
            CancellationToken.None);

        Assert.Equal(SessionClaimPhase.Owned, store.State.Phase);
        Assert.Equal("process-b", store.State.HolderIdentity);
        Assert.Equal(9, store.State.Generation);
    }

    private sealed class InMemoryClaimStore(SessionClaimState initial) : ISessionClaimStore
    {
        private readonly object _gate = new();
        public SessionClaimState State { get; private set; } = initial;

        public Task<SessionClaimState> ReadAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                return Task.FromResult(State);
            }
        }

        public Task<bool> TryReplaceAsync(
            SessionClaimState expected,
            SessionClaimState replacement,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (!string.Equals(State.ResourceVersion, expected.ResourceVersion, StringComparison.Ordinal))
                {
                    return Task.FromResult(false);
                }

                State = replacement with
                {
                    ResourceVersion = (long.Parse(State.ResourceVersion) + 1).ToString(),
                };
                return Task.FromResult(true);
            }
        }
    }
}

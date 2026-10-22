using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Munibot.Tests;

public sealed class MunibotHostedServiceTests
{
    [Fact]
    public async Task ProviderLoginStartsOnlyAfterTheClassWideClaimIsAcquired()
    {
        var claims = new FakeClaims();
        var session = new FakeSession(() => Assert.True(claims.AcquireCompleted));
        var fixture = CreateFixture(session, claims);

        await fixture.Service.StartAsync(CancellationToken.None);
        await session.WaitForLoginAsync();

        Assert.Equal(1, claims.AcquireCalls);
        Assert.Equal(1, session.LoginCalls);
        Assert.True(fixture.Service.IsReady);
        await fixture.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExistingClaimStopsWithoutConnectingToProvider()
    {
        var claims = new FakeClaims { Acquisition = new SessionClaimAcquisition(null, SessionClaimPhase.Owned) };
        var session = new FakeSession();
        var fixture = CreateFixture(session, claims);

        await fixture.Service.StartAsync(CancellationToken.None);
        await fixture.Lifetime.WaitForStopAsync();

        Assert.Equal(0, session.LoginCalls);
        Assert.False(fixture.Service.IsReady);
        await fixture.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task MissingOrPartitionedClaimStoreStopsWithoutConnectingToProvider()
    {
        var claims = new FakeClaims { AcquireException = new HttpRequestException("claim unavailable") };
        var session = new FakeSession();
        var fixture = CreateFixture(session, claims);

        await fixture.Service.StartAsync(CancellationToken.None);
        await fixture.Lifetime.WaitForStopAsync();

        Assert.Equal(0, session.LoginCalls);
        Assert.False(fixture.Service.IsReady);
        await fixture.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AmbiguousLoginFailureRetainsClaimAndStops()
    {
        var claims = new FakeClaims();
        var session = new FakeSession { LoginException = new TimeoutException("ambiguous") };
        var fixture = CreateFixture(session, claims);

        await fixture.Service.StartAsync(CancellationToken.None);
        await fixture.Lifetime.WaitForStopAsync();

        Assert.Equal([ProviderSessionCloseOutcome.Ambiguous], claims.CloseOutcomes);
        Assert.False(fixture.Service.IsReady);
        await fixture.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task UnexpectedDisconnectIsRetainedAsUncertainWithoutReconnect()
    {
        var claims = new FakeClaims();
        var session = new FakeSession();
        var fixture = CreateFixture(session, claims);

        await fixture.Service.StartAsync(CancellationToken.None);
        await session.WaitForLoginAsync();
        await fixture.Time.WaitForNextTimerAsync();
        session.Disconnect();
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        await fixture.Lifetime.WaitForStopAsync();

        Assert.Equal(1, session.LoginCalls);
        Assert.Equal([ProviderSessionCloseOutcome.Ambiguous], claims.CloseOutcomes);
        Assert.False(fixture.Service.IsReady);
        await fixture.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task GracefulStopReleasesOnlyAfterProviderAcknowledgement()
    {
        var claims = new FakeClaims();
        var session = new FakeSession { LogoutOutcome = ProviderSessionCloseOutcome.Acknowledged };
        var fixture = CreateFixture(session, claims);

        await fixture.Service.StartAsync(CancellationToken.None);
        await session.WaitForLoginAsync();
        await fixture.Service.StopAsync(CancellationToken.None);

        Assert.Equal([ProviderSessionCloseOutcome.Acknowledged], claims.CloseOutcomes);
        Assert.False(fixture.Service.IsReady);
    }

    [Fact]
    public async Task LogoutTimeoutRetainsRealClaim()
    {
        var store = new MemoryClaimStore();
        var claims = new SessionClaimCoordinator(store);
        var session = new FakeSession { LogoutException = new TimeoutException("no provider acknowledgement") };
        var fixture = CreateFixture(session, claims);
        await fixture.Service.StartAsync(CancellationToken.None);
        await session.WaitForLoginAsync();
        await fixture.Service.StopAsync(CancellationToken.None);

        Assert.Equal(SessionClaimPhase.Uncertain, store.State.Phase);
        Assert.NotNull(store.State.HolderIdentity);
        Assert.Null((await claims.AcquireAsync("replacement", CancellationToken.None)).Token);
    }

    [Fact]
    public async Task PartitionDuringUnexpectedDisconnectCannotBecomeAnAcknowledgedRelease()
    {
        var claims = new FakeClaims { CloseExceptionCount = 1 };
        var session = new FakeSession { LogoutOutcome = ProviderSessionCloseOutcome.Acknowledged };
        var fixture = CreateFixture(session, claims);

        await fixture.Service.StartAsync(CancellationToken.None);
        await session.WaitForLoginAsync();
        await fixture.Time.WaitForNextTimerAsync();
        session.Disconnect();
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        await fixture.Lifetime.WaitForStopAsync();
        await fixture.Service.StopAsync(CancellationToken.None);

        Assert.Equal(
            [ProviderSessionCloseOutcome.Ambiguous, ProviderSessionCloseOutcome.Ambiguous],
            claims.CloseOutcomes);
        Assert.DoesNotContain(ProviderSessionCloseOutcome.Acknowledged, claims.CloseOutcomes);
    }

    [Fact]
    public async Task RegionShutdownReleasesClaimForAFreshProcess()
    {
        var store = new MemoryClaimStore();
        var claims = new SessionClaimCoordinator(store);
        var session = new FakeSession();
        var fixture = CreateFixture(session, claims);

        await fixture.Service.StartAsync(CancellationToken.None);
        await session.WaitForLoginAsync();
        await fixture.Time.WaitForNextTimerAsync();
        session.Disconnect(SessionDisconnectKind.RegionShutdown);
        Assert.False(fixture.Service.IsReady);
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        await fixture.Time.WaitForNextTimerAsync();
        Assert.Equal(SessionClaimPhase.Owned, store.State.Phase);
        Assert.Null((await claims.AcquireAsync("competing-process", CancellationToken.None)).Token);
        Assert.False(fixture.Lifetime.ApplicationStopping.IsCancellationRequested);
        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        await fixture.Lifetime.WaitForStopAsync();
        await fixture.Service.StopAsync(CancellationToken.None);

        Assert.Equal(SessionClaimPhase.Released, store.State.Phase);
        var replacement = new FakeSession();
        var next = CreateFixture(replacement, new SessionClaimCoordinator(store));
        await next.Service.StartAsync(CancellationToken.None);
        await replacement.WaitForLoginAsync();
        Assert.True(next.Service.IsReady);
        Assert.Equal(2, store.State.Generation);
        Assert.Equal(1, session.LoginCalls);
        Assert.Equal(1, replacement.LoginCalls);
        session.Disconnect(SessionDisconnectKind.RegionShutdown);
        await fixture.Service.StopAsync(CancellationToken.None);
        Assert.Equal(SessionClaimPhase.Owned, store.State.Phase);
        Assert.Equal(2, store.State.Generation);
        await next.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ShutdownNotificationCanArriveAfterTheOfflineObservation()
    {
        var claims = new FakeClaims();
        var session = new FakeSession();
        var fixture = CreateFixture(session, claims);
        await fixture.Service.StartAsync(CancellationToken.None);
        await session.WaitForLoginAsync();
        await fixture.Time.WaitForNextTimerAsync();
        session.DisconnectWithoutNotification();
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        await fixture.Time.WaitForNextTimerAsync();
        Assert.Empty(claims.CloseOutcomes);
        session.Disconnect(SessionDisconnectKind.RegionShutdown);
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        await fixture.Time.WaitForNextTimerAsync();
        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        await fixture.Lifetime.WaitForStopAsync();
        await fixture.Service.StopAsync(CancellationToken.None);
        Assert.Equal([ProviderSessionCloseOutcome.RegionShutdown], claims.CloseOutcomes);
    }

    [Fact]
    public async Task MissingTerminalNotificationStillRetainsClaim()
    {
        var claims = new FakeClaims();
        var session = new FakeSession();
        var fixture = CreateFixture(session, claims);
        await fixture.Service.StartAsync(CancellationToken.None);
        await session.WaitForLoginAsync();
        await fixture.Time.WaitForNextTimerAsync();
        session.DisconnectWithoutNotification();
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        await fixture.Time.WaitForNextTimerAsync();
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        await fixture.Lifetime.WaitForStopAsync();
        await fixture.Service.StopAsync(CancellationToken.None);
        Assert.Equal([ProviderSessionCloseOutcome.Ambiguous], claims.CloseOutcomes);
    }

    [Fact]
    public async Task StopDuringCooldownClosesConfirmedTerminalSessionExactlyOnce()
    {
        var claims = new FakeClaims();
        var session = new FakeSession();
        var fixture = CreateFixture(session, claims);
        await fixture.Service.StartAsync(CancellationToken.None);
        await session.WaitForLoginAsync();
        await fixture.Time.WaitForNextTimerAsync();
        session.Disconnect(SessionDisconnectKind.RegionShutdown);
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        await fixture.Time.WaitForNextTimerAsync();
        await fixture.Service.StopAsync(CancellationToken.None);
        await fixture.Service.StopAsync(CancellationToken.None);
        Assert.Equal([ProviderSessionCloseOutcome.RegionShutdown], claims.CloseOutcomes);
        Assert.False(fixture.Service.IsReady);
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("holder")]
    [InlineData("partition")]
    [InlineData("conflict")]
    public async Task FailedRegionShutdownReleaseCannotEnableAReplacementLogin(string failure)
    {
        var store = new MemoryClaimStore();
        var claims = new SessionClaimCoordinator(store);
        var session = new FakeSession();
        var fixture = CreateFixture(session, claims);
        await fixture.Service.StartAsync(CancellationToken.None);
        await session.WaitForLoginAsync();
        await fixture.Time.WaitForNextTimerAsync();
        session.Disconnect(SessionDisconnectKind.RegionShutdown);
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        await fixture.Time.WaitForNextTimerAsync();

        if (failure == "generation") store.State = store.State with { Generation = 0 };
        if (failure == "holder") store.State = store.State with { HolderIdentity = "other-process" };
        store.FailWrites = failure == "partition";
        store.ChangeOwnerOnWrite = failure == "conflict";
        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        await fixture.Lifetime.WaitForStopAsync();
        await fixture.Service.StopAsync(CancellationToken.None);
        Assert.Equal(SessionClaimPhase.Owned, store.State.Phase);
        Assert.NotNull(store.State.HolderIdentity);

        var replacement = new FakeSession();
        var next = CreateFixture(replacement, claims);
        await next.Service.StartAsync(CancellationToken.None);
        await next.Lifetime.WaitForStopAsync();
        Assert.Equal(0, replacement.LoginCalls);
        Assert.False(next.Service.IsReady);
        await next.Service.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData((int)SessionDisconnectKind.NetworkTimeout)]
    [InlineData((int)SessionDisconnectKind.Other)]
    public async Task AmbiguousDisconnectKeepsRealClaimAndBlocksNextProcess(int kind)
    {
        var store = new MemoryClaimStore();
        var claims = new SessionClaimCoordinator(store);
        var session = new FakeSession();
        var fixture = CreateFixture(session, claims);
        await fixture.Service.StartAsync(CancellationToken.None);
        await session.WaitForLoginAsync();
        await fixture.Time.WaitForNextTimerAsync();
        session.Disconnect((SessionDisconnectKind)kind);
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        await fixture.Lifetime.WaitForStopAsync();
        await fixture.Service.StopAsync(CancellationToken.None);
        Assert.Equal(SessionClaimPhase.Uncertain, store.State.Phase);
        Assert.Null((await claims.AcquireAsync("replacement", CancellationToken.None)).Token);
    }

    private sealed class MemoryClaimStore : ISessionClaimStore
    {
        public SessionClaimState State { get; set; } = SessionClaimState.Released("1");
        public bool FailWrites { get; set; }
        public bool ChangeOwnerOnWrite { get; set; }

        public Task<SessionClaimState> ReadAsync(CancellationToken cancellationToken)
            => Task.FromResult(State);

        public Task<bool> TryReplaceAsync(SessionClaimState expected, SessionClaimState replacement, CancellationToken cancellationToken)
        {
            if (FailWrites) throw new HttpRequestException("claim unavailable");
            if (ChangeOwnerOnWrite)
            {
                ChangeOwnerOnWrite = false;
                State = State with { ResourceVersion = "100", HolderIdentity = "other-process", Generation = 20 };
                return Task.FromResult(false);
            }
            if (State.ResourceVersion != expected.ResourceVersion) return Task.FromResult(false);
            State = replacement with { ResourceVersion = (long.Parse(State.ResourceVersion) + 1).ToString() };
            return Task.FromResult(true);
        }
    }

    private static Fixture CreateFixture(FakeSession session, ISessionClaimCoordinator claims)
    {
        var time = new SignalingFakeTimeProvider();
        var lifetime = new FakeApplicationLifetime();
        var service = new MunibotHostedService(
            new BotConfig { Runtime = new BotRuntimeConfig { MovementKeepaliveSeconds = 0 } },
            session,
            claims,
            new SessionProcessIdentity(Guid.NewGuid().ToString("N")),
            new TestLogger<MunibotHostedService>(),
            lifetime,
            time);
        return new Fixture(service, lifetime, time);
    }

    private sealed record Fixture(
        MunibotHostedService Service,
        FakeApplicationLifetime Lifetime,
        SignalingFakeTimeProvider Time);

    private sealed class FakeClaims : ISessionClaimCoordinator
    {
        public SessionClaimAcquisition Acquisition { get; init; } = new(
            new SessionClaimToken("opaque-test-nonce", 1),
            SessionClaimPhase.Owned);
        public bool AcquireCompleted { get; private set; }
        public int AcquireCalls { get; private set; }
        public Exception? AcquireException { get; init; }
        public int CloseExceptionCount { get; set; }
        public List<ProviderSessionCloseOutcome> CloseOutcomes { get; } = [];

        public Task<SessionClaimAcquisition> AcquireAsync(string processNonce, CancellationToken cancellationToken)
        {
            AcquireCalls++;
            if (AcquireException is not null)
            {
                throw AcquireException;
            }

            AcquireCompleted = true;
            return Task.FromResult(Acquisition);
        }

        public Task<bool> CloseAsync(SessionClaimToken token, ProviderSessionCloseOutcome outcome, CancellationToken cancellationToken)
        {
            CloseOutcomes.Add(outcome);
            if (CloseExceptionCount > 0)
            {
                CloseExceptionCount--;
                throw new HttpRequestException("claim update unavailable");
            }

            return Task.FromResult(true);
        }
    }

    private sealed class FakeSession(Action? beforeLogin = null) : IMunibotSessionLifecycle
    {
        private readonly SemaphoreSlim _loginSignal = new(0);

        public bool IsOnline { get; private set; }
        public SessionDisconnectSnapshot? LastDisconnect { get; private set; }
        public int LoginCalls { get; private set; }
        public Exception? LoginException { get; init; }
        public Exception? LogoutException { get; init; }
        public ProviderSessionCloseOutcome LogoutOutcome { get; init; } = ProviderSessionCloseOutcome.Ambiguous;

        public Task LoginAsync(CancellationToken cancellationToken)
        {
            beforeLogin?.Invoke();
            LoginCalls++;
            _loginSignal.Release();
            if (LoginException is not null)
            {
                throw LoginException;
            }

            IsOnline = true;
            return Task.CompletedTask;
        }

        public async Task WaitForLoginAsync()
        {
            var observed = await _loginSignal.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(observed, "The hosted service did not attempt login.");
        }

        public void Disconnect(SessionDisconnectKind kind = SessionDisconnectKind.NetworkTimeout)
        {
            IsOnline = false;
            LastDisconnect = new SessionDisconnectSnapshot(kind, DateTimeOffset.UtcNow, null);
        }

        public void DisconnectWithoutNotification() => IsOnline = false;

        public bool SendMovementKeepalive() => false;
        public SessionTransportSnapshot? CaptureTransportSnapshot() => null;

        public Task<ProviderSessionCloseOutcome> LogoutAsync(CancellationToken cancellationToken)
        {
            if (LogoutException is not null) throw LogoutException;
            IsOnline = false;
            return Task.FromResult(LogoutOutcome);
        }
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }

    private sealed class FakeApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication()
        {
            _stopping.Cancel();
            _stopped.Cancel();
        }

        public async Task WaitForStopAsync()
        {
            var stopped = await Task.Run(() => _stopped.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)));
            Assert.True(stopped, "The hosted service did not stop the application.");
        }
    }

    private sealed class SignalingFakeTimeProvider : FakeTimeProvider
    {
        private readonly SemaphoreSlim _timerSignals = new(0);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            _timerSignals.Release();
            return timer;
        }

        public async Task WaitForNextTimerAsync()
        {
            var observed = await _timerSignals.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(observed, "The hosted service did not register its next timer.");
        }
    }
}

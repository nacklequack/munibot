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

    private static Fixture CreateFixture(FakeSession session, FakeClaims claims)
    {
        var time = new SignalingFakeTimeProvider();
        var lifetime = new FakeApplicationLifetime();
        var service = new MunibotHostedService(
            new BotConfig { Runtime = new BotRuntimeConfig { MovementKeepaliveSeconds = 0 } },
            session,
            claims,
            new SessionProcessIdentity("opaque-test-nonce"),
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
        public List<ProviderSessionCloseOutcome> CloseOutcomes { get; } = [];

        public Task<SessionClaimAcquisition> AcquireAsync(string processNonce, CancellationToken cancellationToken)
        {
            AcquireCalls++;
            AcquireCompleted = true;
            return Task.FromResult(Acquisition);
        }

        public Task CloseAsync(SessionClaimToken token, ProviderSessionCloseOutcome outcome, CancellationToken cancellationToken)
        {
            CloseOutcomes.Add(outcome);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSession(Action? beforeLogin = null) : IMunibotSessionLifecycle
    {
        private readonly SemaphoreSlim _loginSignal = new(0);

        public bool IsOnline { get; private set; }
        public SessionDisconnectSnapshot? LastDisconnect { get; private set; }
        public int LoginCalls { get; private set; }
        public Exception? LoginException { get; init; }
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

        public void Disconnect()
        {
            IsOnline = false;
            LastDisconnect = new SessionDisconnectSnapshot(SessionDisconnectKind.NetworkTimeout, DateTimeOffset.UtcNow, null);
        }

        public bool SendMovementKeepalive() => false;
        public SessionTransportSnapshot? CaptureTransportSnapshot() => null;

        public Task<ProviderSessionCloseOutcome> LogoutAsync(CancellationToken cancellationToken)
        {
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

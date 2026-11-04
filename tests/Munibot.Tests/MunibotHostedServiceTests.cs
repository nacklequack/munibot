using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Munibot.Tests;

public sealed class MunibotHostedServiceTests
{
    [Fact]
    public async Task ProviderLoginStartsWhenTheServiceStarts()
    {
        var session = new FakeSession();
        var fixture = CreateFixture(session);

        await fixture.Service.StartAsync(CancellationToken.None);
        await session.WaitForLoginAsync();

        Assert.Equal(1, session.LoginCalls);
        Assert.True(fixture.Service.IsReady);
        await fixture.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task LoginFailureStopsSoKubernetesCanStartAFreshProcess()
    {
        var session = new FakeSession { LoginException = new TimeoutException("login failed") };
        var fixture = CreateFixture(session);

        await fixture.Service.StartAsync(CancellationToken.None);
        await fixture.Lifetime.WaitForStopAsync();

        Assert.Equal(1, session.LoginCalls);
        Assert.False(fixture.Service.IsReady);
        await fixture.Service.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData((int)SessionDisconnectKind.NetworkTimeout)]
    [InlineData((int)SessionDisconnectKind.Other)]
    public async Task UnexpectedDisconnectStopsSoKubernetesCanStartAFreshProcess(int kind)
    {
        var session = new FakeSession();
        var fixture = CreateFixture(session);

        await fixture.Service.StartAsync(CancellationToken.None);
        await session.WaitForLoginAsync();
        await fixture.Time.WaitForNextTimerAsync();
        session.Disconnect((SessionDisconnectKind)kind);
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        await fixture.Lifetime.WaitForStopAsync();

        Assert.Equal(1, session.LoginCalls);
        Assert.False(fixture.Service.IsReady);
        await fixture.Service.StopAsync(CancellationToken.None);

        var replacement = new FakeSession();
        var next = CreateFixture(replacement);
        await next.Service.StartAsync(CancellationToken.None);
        await replacement.WaitForLoginAsync();

        Assert.Equal(1, replacement.LoginCalls);
        Assert.True(next.Service.IsReady);
        await next.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task GracefulStopLogsOut()
    {
        var session = new FakeSession();
        var fixture = CreateFixture(session);

        await fixture.Service.StartAsync(CancellationToken.None);
        await session.WaitForLoginAsync();
        await fixture.Service.StopAsync(CancellationToken.None);

        Assert.Equal(1, session.LogoutCalls);
        Assert.False(fixture.Service.IsReady);
    }

    [Fact]
    public async Task LogoutFailureDoesNotCreatePersistentOwnershipState()
    {
        var session = new FakeSession { LogoutException = new TimeoutException("logout failed") };
        var fixture = CreateFixture(session);

        await fixture.Service.StartAsync(CancellationToken.None);
        await session.WaitForLoginAsync();
        await fixture.Service.StopAsync(CancellationToken.None);

        Assert.Equal(1, session.LogoutCalls);
        Assert.False(fixture.Service.IsReady);

        var replacement = new FakeSession();
        var next = CreateFixture(replacement);
        await next.Service.StartAsync(CancellationToken.None);
        await replacement.WaitForLoginAsync();
        Assert.True(next.Service.IsReady);
        await next.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RegionShutdownWaitsBeforeStartingAFreshProcess()
    {
        var session = new FakeSession();
        var fixture = CreateFixture(session);

        await fixture.Service.StartAsync(CancellationToken.None);
        await session.WaitForLoginAsync();
        await fixture.Time.WaitForNextTimerAsync();
        session.Disconnect(SessionDisconnectKind.RegionShutdown);
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        await fixture.Time.WaitForNextTimerAsync();

        Assert.False(fixture.Service.IsReady);
        Assert.False(fixture.Lifetime.ApplicationStopping.IsCancellationRequested);

        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        await fixture.Lifetime.WaitForStopAsync();
        await fixture.Service.StopAsync(CancellationToken.None);

        var replacement = new FakeSession();
        var next = CreateFixture(replacement);
        await next.Service.StartAsync(CancellationToken.None);
        await replacement.WaitForLoginAsync();

        Assert.Equal(1, replacement.LoginCalls);
        Assert.True(next.Service.IsReady);
        await next.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ShutdownNotificationCanArriveAfterTheOfflineObservation()
    {
        var session = new FakeSession();
        var fixture = CreateFixture(session);

        await fixture.Service.StartAsync(CancellationToken.None);
        await session.WaitForLoginAsync();
        await fixture.Time.WaitForNextTimerAsync();
        session.DisconnectWithoutNotification();
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        await fixture.Time.WaitForNextTimerAsync();

        Assert.False(fixture.Lifetime.ApplicationStopping.IsCancellationRequested);

        session.Disconnect(SessionDisconnectKind.RegionShutdown);
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        await fixture.Time.WaitForNextTimerAsync();
        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        await fixture.Lifetime.WaitForStopAsync();
        await fixture.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task MissingTerminalNotificationStillStartsAFreshProcess()
    {
        var session = new FakeSession();
        var fixture = CreateFixture(session);

        await fixture.Service.StartAsync(CancellationToken.None);
        await session.WaitForLoginAsync();
        await fixture.Time.WaitForNextTimerAsync();
        session.DisconnectWithoutNotification();
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        await fixture.Time.WaitForNextTimerAsync();
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        await fixture.Lifetime.WaitForStopAsync();
        await fixture.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopDuringRegionShutdownCooldownDoesNotLogoutAnOfflineSession()
    {
        var session = new FakeSession();
        var fixture = CreateFixture(session);

        await fixture.Service.StartAsync(CancellationToken.None);
        await session.WaitForLoginAsync();
        await fixture.Time.WaitForNextTimerAsync();
        session.Disconnect(SessionDisconnectKind.RegionShutdown);
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        await fixture.Time.WaitForNextTimerAsync();
        await fixture.Service.StopAsync(CancellationToken.None);
        await fixture.Service.StopAsync(CancellationToken.None);

        Assert.Equal(0, session.LogoutCalls);
        Assert.False(fixture.Service.IsReady);
    }

    private static Fixture CreateFixture(FakeSession session)
    {
        var time = new SignalingFakeTimeProvider();
        var lifetime = new FakeApplicationLifetime();
        var service = new MunibotHostedService(
            new BotConfig { Runtime = new BotRuntimeConfig { MovementKeepaliveSeconds = 0 } },
            session,
            new TestLogger<MunibotHostedService>(),
            lifetime,
            time);
        return new Fixture(service, lifetime, time);
    }

    private sealed record Fixture(
        MunibotHostedService Service,
        FakeApplicationLifetime Lifetime,
        SignalingFakeTimeProvider Time);

    private sealed class FakeSession : IMunibotSessionLifecycle
    {
        private readonly SemaphoreSlim _loginSignal = new(0);

        public bool IsOnline { get; private set; }
        public SessionDisconnectSnapshot? LastDisconnect { get; private set; }
        public int LoginCalls { get; private set; }
        public int LogoutCalls { get; private set; }
        public Exception? LoginException { get; init; }
        public Exception? LogoutException { get; init; }

        public Task LoginAsync(CancellationToken cancellationToken)
        {
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

        public void Disconnect(SessionDisconnectKind kind)
        {
            IsOnline = false;
            LastDisconnect = new SessionDisconnectSnapshot(kind, DateTimeOffset.UtcNow, null);
        }

        public void DisconnectWithoutNotification() => IsOnline = false;

        public bool SendMovementKeepalive() => false;
        public SessionTransportSnapshot? CaptureTransportSnapshot() => null;

        public Task LogoutAsync(CancellationToken cancellationToken)
        {
            if (!IsOnline)
            {
                return Task.CompletedTask;
            }

            LogoutCalls++;
            if (LogoutException is not null)
            {
                throw LogoutException;
            }

            IsOnline = false;
            return Task.CompletedTask;
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

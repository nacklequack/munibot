using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Munibot.Tests;

public sealed class MunibotHostedServiceTests
{
    [Fact]
    public async Task OfflineSessionUsesConfiguredFixedReconnectDelay()
    {
        var time = new SignalingFakeTimeProvider();
        var session = new FakeSession();
        var logger = new TestLogger<MunibotHostedService>();
        var config = new BotConfig
        {
            Runtime = new BotRuntimeConfig
            {
                ReconnectDelaySeconds = 15,
                MovementKeepaliveSeconds = 0,
            },
        };
        var service = new MunibotHostedService(
            config,
            session,
            logger,
            new FakeApplicationLifetime(),
            time);
        using var cancellation = new CancellationTokenSource();

        await service.StartAsync(cancellation.Token);
        await session.WaitForNextLoginAsync();
        await time.WaitForNextTimerAsync();
        session.Disconnect(SessionDisconnectKind.Other, time.GetUtcNow());

        time.Advance(TimeSpan.FromSeconds(5));
        await logger.WaitForReconnectScheduledAsync();
        await time.WaitForNextTimerAsync();
        time.Advance(TimeSpan.FromSeconds(14));
        await Task.Yield();
        Assert.Equal(1, session.LoginCalls);

        time.Advance(TimeSpan.FromSeconds(1));
        await session.WaitForNextLoginAsync();
        Assert.Equal(2, session.LoginCalls);

        cancellation.Cancel();
        time.Advance(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        private readonly SemaphoreSlim _reconnectScheduled = new(0);

        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            Messages.Add(message);
            if (message.Contains("attempting reconnect", StringComparison.Ordinal))
            {
                _reconnectScheduled.Release();
            }
        }

        public async Task WaitForReconnectScheduledAsync()
        {
            var observed = await _reconnectScheduled.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(observed, "The hosted service did not schedule a reconnect.");
        }
    }

    private sealed class FakeSession : IMunibotSessionLifecycle
    {
        private readonly SemaphoreSlim _loginSignals = new(0);
        private int _loginCalls;

        public bool IsOnline { get; private set; }
        public SessionDisconnectSnapshot? LastDisconnect { get; private set; }
        public int LoginCalls => Volatile.Read(ref _loginCalls);

        public Task LoginAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _loginCalls);
            IsOnline = true;
            _loginSignals.Release();
            return Task.CompletedTask;
        }

        public async Task WaitForNextLoginAsync()
        {
            var observed = await _loginSignals.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(observed, "The hosted service did not attempt login.");
        }

        public void Disconnect(SessionDisconnectKind kind, DateTimeOffset occurredAt)
        {
            IsOnline = false;
            LastDisconnect = new SessionDisconnectSnapshot(kind, occurredAt, null);
        }

        public bool SendMovementKeepalive() => false;
        public SessionTransportSnapshot? CaptureTransportSnapshot() => null;
        public void Logout() => IsOnline = false;
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
    }

    private sealed class SignalingFakeTimeProvider : FakeTimeProvider
    {
        private readonly SemaphoreSlim _timerSignals = new(0);

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
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

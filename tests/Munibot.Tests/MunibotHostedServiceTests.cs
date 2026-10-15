using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Munibot.Tests;

public sealed class MunibotHostedServiceTests
{
    [Fact]
    public async Task InitialLoginFailuresRetainFixedBaseDelay()
    {
        var time = new SignalingFakeTimeProvider();
        var session = new FakeSession { LoginFailuresRemaining = 2 };
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

        try
        {
            await service.StartAsync(cancellation.Token);
            await session.WaitForNextLoginAsync();

            foreach (var expectedAttempt in new[] { 2, 3 })
            {
                await logger.WaitForInitialRetryScheduledAsync();
                await time.WaitForNextTimerAsync();
                Assert.Equal(15, logger.InitialLoginDelays[^1]);
                time.Advance(TimeSpan.FromSeconds(15));
                await session.WaitForNextLoginAsync();
                Assert.Equal(expectedAttempt, session.LoginCalls);
            }

            await time.WaitForNextTimerAsync();
        }
        finally
        {
            cancellation.Cancel();
            time.Advance(TimeSpan.FromSeconds(5));
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ConsecutiveNetworkTimeoutsUseExponentialReconnectDelays()
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

        try
        {
            await service.StartAsync(cancellation.Token);
            await session.WaitForNextLoginAsync();
            await time.WaitForNextTimerAsync();

            foreach (var expectedDelaySeconds in new[] { 15, 30, 60 })
            {
                session.Disconnect(SessionDisconnectKind.NetworkTimeout, time.GetUtcNow());
                time.Advance(TimeSpan.FromSeconds(5));
                await logger.WaitForReconnectScheduledAsync();
                await time.WaitForNextTimerAsync();

                Assert.Equal(expectedDelaySeconds, logger.ReconnectDelays[^1]);

                time.Advance(TimeSpan.FromSeconds(expectedDelaySeconds));
                await session.WaitForNextLoginAsync();
                await time.WaitForNextTimerAsync();
            }
        }
        finally
        {
            cancellation.Cancel();
            time.Advance(TimeSpan.FromSeconds(5));
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StableConnectionResetsNetworkTimeoutDelay()
    {
        var time = new SignalingFakeTimeProvider();
        var session = new FakeSession(time);
        var logger = new TestLogger<MunibotHostedService>();
        var config = new BotConfig
        {
            Runtime = new BotRuntimeConfig
            {
                ReconnectDelaySeconds = 15,
                MaxReconnectDelaySeconds = 300,
                ReconnectResetAfterSeconds = 600,
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

        try
        {
            await service.StartAsync(cancellation.Token);
            await session.WaitForNextLoginAsync();
            await time.WaitForNextTimerAsync();

            foreach (var expectedDelaySeconds in new[] { 15, 30 })
            {
                session.Disconnect(SessionDisconnectKind.NetworkTimeout, time.GetUtcNow());
                time.Advance(TimeSpan.FromSeconds(5));
                await logger.WaitForReconnectScheduledAsync();
                await time.WaitForNextTimerAsync();
                Assert.Equal(expectedDelaySeconds, logger.ReconnectDelays[^1]);
                time.Advance(TimeSpan.FromSeconds(expectedDelaySeconds));
                await session.WaitForNextLoginAsync();
                await time.WaitForNextTimerAsync();
            }

            time.Advance(TimeSpan.FromSeconds(600));
            await logger.WaitForStableConnectionAsync();
            await time.WaitForNextTimerAsync();

            session.Disconnect(SessionDisconnectKind.NetworkTimeout, time.GetUtcNow());
            time.Advance(TimeSpan.FromSeconds(5));
            await logger.WaitForReconnectScheduledAsync();
            await time.WaitForNextTimerAsync();

            Assert.Equal(15, logger.ReconnectDelays[^1]);
        }
        finally
        {
            cancellation.Cancel();
            time.Advance(TimeSpan.FromSeconds(300));
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task CancellationStopsPendingReconnectDelay()
    {
        var time = new SignalingFakeTimeProvider();
        var session = new FakeSession(time);
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
        session.Disconnect(SessionDisconnectKind.NetworkTimeout, time.GetUtcNow());
        time.Advance(TimeSpan.FromSeconds(5));
        await logger.WaitForReconnectScheduledAsync();
        await time.WaitForNextTimerAsync();

        cancellation.Cancel();
        await service.StopAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromHours(1));

        Assert.Equal(1, session.LoginCalls);
    }

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
        private readonly SemaphoreSlim _initialRetryScheduled = new(0);
        private readonly SemaphoreSlim _reconnectScheduled = new(0);
        private readonly SemaphoreSlim _stableConnection = new(0);

        public List<string> Messages { get; } = [];
        public List<int> InitialLoginDelays { get; } = [];
        public List<int> ReconnectDelays { get; } = [];

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
            if (message.Contains("Retrying Munibot login", StringComparison.Ordinal))
            {
                if (TryGetDelaySeconds(state, out var delaySeconds))
                {
                    InitialLoginDelays.Add(delaySeconds);
                }

                _initialRetryScheduled.Release();
            }

            if (message.Contains("attempting reconnect", StringComparison.Ordinal))
            {
                if (TryGetDelaySeconds(state, out var delaySeconds))
                {
                    ReconnectDelays.Add(delaySeconds);
                }

                _reconnectScheduled.Release();
            }

            if (message.Contains("connection stable", StringComparison.Ordinal))
            {
                _stableConnection.Release();
            }
        }

        private static bool TryGetDelaySeconds<TState>(TState state, out int delaySeconds)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> properties &&
                properties.FirstOrDefault(property => property.Key == "DelaySeconds").Value is { } value)
            {
                delaySeconds = Convert.ToInt32(value);
                return true;
            }

            delaySeconds = 0;
            return false;
        }

        public async Task WaitForInitialRetryScheduledAsync()
        {
            var observed = await _initialRetryScheduled.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(observed, "The hosted service did not schedule an initial login retry.");
        }

        public async Task WaitForReconnectScheduledAsync()
        {
            var observed = await _reconnectScheduled.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(observed, "The hosted service did not schedule a reconnect.");
        }

        public async Task WaitForStableConnectionAsync()
        {
            var observed = await _stableConnection.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(observed, "The hosted service did not observe a stable connection.");
        }
    }

    private sealed class FakeSession : IMunibotSessionLifecycle
    {
        private readonly SemaphoreSlim _loginSignals = new(0);
        private readonly TimeProvider? _timeProvider;
        private int _loginCalls;
        private long _connectionGeneration;
        private DateTimeOffset _connectedAt;

        public FakeSession(TimeProvider? timeProvider = null)
        {
            _timeProvider = timeProvider;
        }

        public bool IsOnline { get; private set; }
        public SessionDisconnectSnapshot? LastDisconnect { get; private set; }
        public int LoginCalls => Volatile.Read(ref _loginCalls);
        public int LoginFailuresRemaining { get; set; }

        public Task LoginAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _loginCalls);
            _loginSignals.Release();
            if (LoginFailuresRemaining > 0)
            {
                LoginFailuresRemaining--;
                throw new InvalidOperationException("Configured fake login failure.");
            }

            _connectionGeneration++;
            _connectedAt = _timeProvider?.GetUtcNow() ?? DateTimeOffset.UtcNow;
            IsOnline = true;
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
        public SessionTransportSnapshot? CaptureTransportSnapshot()
        {
            if (!IsOnline || _timeProvider is null)
            {
                return null;
            }

            var now = _timeProvider.GetUtcNow();
            return new SessionTransportSnapshot(
                _connectionGeneration,
                _connectedAt,
                now,
                now - _connectedAt,
                null,
                0,
                0,
                0,
                0);
        }
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

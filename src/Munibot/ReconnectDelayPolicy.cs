namespace Munibot;

internal sealed record ReconnectDelayDecision(
    TimeSpan Delay,
    SessionDisconnectKind? DisconnectKind,
    int NetworkTimeoutStreak);

internal sealed class ReconnectDelayPolicy(
    TimeSpan baseDelay,
    TimeSpan maximumDelay)
{
    private readonly TimeSpan _maximumDelay = maximumDelay < baseDelay ? baseDelay : maximumDelay;
    private SessionDisconnectSnapshot? _lastHandledDisconnect;
    private int _networkTimeoutStreak;

    public ReconnectDelayDecision GetDelay(SessionDisconnectSnapshot? disconnect)
    {
        if (disconnect is null || ReferenceEquals(disconnect, _lastHandledDisconnect))
        {
            return new ReconnectDelayDecision(baseDelay, null, _networkTimeoutStreak);
        }

        _lastHandledDisconnect = disconnect;
        if (disconnect.Kind != SessionDisconnectKind.NetworkTimeout)
        {
            _networkTimeoutStreak = 0;
            return new ReconnectDelayDecision(baseDelay, disconnect.Kind, 0);
        }

        _networkTimeoutStreak++;
        var delay = baseDelay;
        for (var timeout = 1; timeout < _networkTimeoutStreak && delay < _maximumDelay; timeout++)
        {
            var doubledTicks = Math.Min(delay.Ticks * 2, _maximumDelay.Ticks);
            delay = TimeSpan.FromTicks(doubledTicks);
        }

        return new ReconnectDelayDecision(delay, disconnect.Kind, _networkTimeoutStreak);
    }

    public void MarkHandled(SessionDisconnectSnapshot? disconnect)
    {
        if (disconnect is not null)
        {
            _lastHandledDisconnect = disconnect;
        }
    }

    public void ResetTimeoutStreak() => _networkTimeoutStreak = 0;
}

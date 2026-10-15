namespace Munibot;

internal enum SessionDisconnectKind
{
    Other,
    NetworkTimeout,
}

internal sealed record SessionTransportCounters(
    long ReceivedPackets,
    long SentPackets,
    int ReceivedResends,
    int ResentPackets);

internal sealed record SessionTransportSnapshot(
    long ConnectionGeneration,
    DateTimeOffset ConnectedAt,
    DateTimeOffset CapturedAt,
    TimeSpan ConnectedDuration,
    TimeSpan? LastReceivedPacketAge,
    long ReceivedPackets,
    long SentPackets,
    int ReceivedResends,
    int ResentPackets);

internal sealed record SessionDisconnectSnapshot(
    SessionDisconnectKind Kind,
    DateTimeOffset OccurredAt,
    SessionTransportSnapshot? Transport);

internal interface IMunibotSessionLifecycle
{
    bool IsOnline { get; }
    SessionDisconnectSnapshot? LastDisconnect { get; }
    Task LoginAsync(CancellationToken cancellationToken);
    bool SendMovementKeepalive();
    SessionTransportSnapshot? CaptureTransportSnapshot();
    Task<ProviderSessionCloseOutcome> LogoutAsync(CancellationToken cancellationToken);
}

internal sealed record SessionProcessIdentity(string Nonce);

internal sealed class SessionTransportDiagnostics(TimeProvider timeProvider)
{
    private readonly object _lock = new();
    private long _connectionGeneration;
    private DateTimeOffset? _connectedAt;
    private DateTimeOffset? _lastReceivedAt;

    public void StartConnection()
    {
        lock (_lock)
        {
            _connectionGeneration++;
            _connectedAt = timeProvider.GetUtcNow();
            _lastReceivedAt = null;
        }
    }

    public void RecordReceivedPacket()
    {
        lock (_lock)
        {
            if (_connectedAt.HasValue)
            {
                _lastReceivedAt = timeProvider.GetUtcNow();
            }
        }
    }

    public SessionTransportSnapshot? Capture(SessionTransportCounters counters)
    {
        lock (_lock)
        {
            if (!_connectedAt.HasValue)
            {
                return null;
            }

            var capturedAt = timeProvider.GetUtcNow();
            return new SessionTransportSnapshot(
                _connectionGeneration,
                _connectedAt.Value,
                capturedAt,
                capturedAt - _connectedAt.Value,
                _lastReceivedAt.HasValue ? capturedAt - _lastReceivedAt.Value : null,
                counters.ReceivedPackets,
                counters.SentPackets,
                counters.ReceivedResends,
                counters.ResentPackets);
        }
    }

    public SessionDisconnectSnapshot CaptureDisconnect(
        SessionDisconnectKind kind,
        SessionTransportCounters counters)
        => new(kind, timeProvider.GetUtcNow(), Capture(counters));
}

using Microsoft.Extensions.Time.Testing;

namespace Munibot.Tests;

public sealed class SessionTransportDiagnosticsTests
{
    [Fact]
    public void CaptureReportsConnectionAgePacketAgeAndCounters()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
        var diagnostics = new SessionTransportDiagnostics(time);

        diagnostics.StartConnection();
        time.Advance(TimeSpan.FromSeconds(7));
        diagnostics.RecordReceivedPacket();
        time.Advance(TimeSpan.FromSeconds(3));

        var snapshot = diagnostics.Capture(new SessionTransportCounters(23, 17, 2, 4));

        Assert.NotNull(snapshot);
        Assert.Equal(1, snapshot.ConnectionGeneration);
        Assert.Equal(TimeSpan.FromSeconds(10), snapshot.ConnectedDuration);
        Assert.Equal(TimeSpan.FromSeconds(3), snapshot.LastReceivedPacketAge);
        Assert.Equal(23, snapshot.ReceivedPackets);
        Assert.Equal(17, snapshot.SentPackets);
        Assert.Equal(2, snapshot.ReceivedResends);
        Assert.Equal(4, snapshot.ResentPackets);
    }

    [Fact]
    public void StartingAnotherConnectionResetsReceiveAgeAndIncrementsGeneration()
    {
        var time = new FakeTimeProvider();
        var diagnostics = new SessionTransportDiagnostics(time);
        diagnostics.StartConnection();
        diagnostics.RecordReceivedPacket();

        time.Advance(TimeSpan.FromMinutes(1));
        diagnostics.StartConnection();

        var snapshot = diagnostics.Capture(new SessionTransportCounters(5, 8, 0, 0));

        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot.ConnectionGeneration);
        Assert.Equal(TimeSpan.Zero, snapshot.ConnectedDuration);
        Assert.Null(snapshot.LastReceivedPacketAge);
    }

    [Fact]
    public void DisconnectSnapshotIncludesTypedReasonAndLatestTransportCounters()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
        var diagnostics = new SessionTransportDiagnostics(time);
        diagnostics.StartConnection();
        diagnostics.RecordReceivedPacket();
        time.Advance(TimeSpan.FromSeconds(60));

        var disconnect = diagnostics.CaptureDisconnect(
            SessionDisconnectKind.NetworkTimeout,
            new SessionTransportCounters(120, 45, 6, 3));

        Assert.Equal(SessionDisconnectKind.NetworkTimeout, disconnect.Kind);
        Assert.Equal(time.GetUtcNow(), disconnect.OccurredAt);
        Assert.NotNull(disconnect.Transport);
        Assert.Equal(TimeSpan.FromSeconds(60), disconnect.Transport.ConnectedDuration);
        Assert.Equal(TimeSpan.FromSeconds(60), disconnect.Transport.LastReceivedPacketAge);
        Assert.Equal(120, disconnect.Transport.ReceivedPackets);
        Assert.Equal(45, disconnect.Transport.SentPackets);
        Assert.Equal(6, disconnect.Transport.ReceivedResends);
        Assert.Equal(3, disconnect.Transport.ResentPackets);
    }

    [Fact]
    public void CaptureBeforeAConnectionReturnsNull()
    {
        var diagnostics = new SessionTransportDiagnostics(new FakeTimeProvider());

        Assert.Null(diagnostics.Capture(new SessionTransportCounters(0, 0, 0, 0)));
    }
}

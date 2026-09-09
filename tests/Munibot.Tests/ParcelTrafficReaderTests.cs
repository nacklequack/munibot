using OpenMetaverse;

namespace Munibot.Tests;

public sealed class ParcelTrafficReaderTests
{
    private static readonly UUID ParcelId = UUID.Parse("11111111-1111-1111-1111-111111111111");

    [Theory]
    [InlineData(0f)]
    [InlineData(123.75f)]
    public async Task Read_PreservesZeroAndFractionalScores_AndUnsubscribes(float dwell)
    {
        var source = new FakeSource();
        source.OnRequest = id =>
        {
            source.Deliver(UUID.Random(), 999);
            source.Deliver(id, dwell);
        };
        var before = DateTimeOffset.UtcNow;
        var result = await new ParcelTrafficReader(source).ReadAsync(ParcelId.ToString(), TimeSpan.FromSeconds(1));

        Assert.Equal((double)dwell, result.Dwell);
        Assert.Equal(ParcelId.ToString(), result.ParcelId);
        Assert.Equal("Example Region", result.RegionName);
        Assert.Equal("ParcelInfoReply", result.Source);
        Assert.InRange(result.ObservedAt, before, DateTimeOffset.UtcNow);
        Assert.Equal(0, source.Subscribers);
    }

    [Theory]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public async Task InvalidScore_IsAnErrorRatherThanZero(float dwell)
    {
        var source = new FakeSource();
        source.OnRequest = id => source.Deliver(id, dwell);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ParcelTrafficReader(source).ReadAsync(ParcelId.ToString(), TimeSpan.FromSeconds(1)));
        Assert.Equal(0, source.Subscribers);
    }

    [Fact]
    public async Task Timeout_Unsubscribes_AndDoesNotAcceptAnotherParcel()
    {
        var source = new FakeSource();
        source.OnRequest = _ => source.Deliver(UUID.Random(), 12);
        await Assert.ThrowsAsync<TimeoutException>(() =>
            new ParcelTrafficReader(source).ReadAsync(ParcelId.ToString(), TimeSpan.FromMilliseconds(20)));
        Assert.Equal(0, source.Subscribers);
    }

    [Fact]
    public async Task Cancellation_Unsubscribes()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new FakeSource { OnRequest = _ => cancellation.Cancel() };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ParcelTrafficReader(source).ReadAsync(ParcelId.ToString(), TimeSpan.FromSeconds(1), cancellation.Token));
        Assert.Equal(0, source.Subscribers);
    }

    [Fact]
    public async Task SendFailure_Unsubscribes()
    {
        var source = new FakeSource { OnRequest = _ => throw new IOException("send failed") };
        await Assert.ThrowsAsync<IOException>(() =>
            new ParcelTrafficReader(source).ReadAsync(ParcelId.ToString(), TimeSpan.FromSeconds(1)));
        Assert.Equal(0, source.Subscribers);
    }

    [Theory]
    [InlineData("not-a-uuid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task InvalidId_DoesNotSend(string id)
    {
        var source = new FakeSource { OnRequest = _ => throw new Exception("must not send") };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new ParcelTrafficReader(source).ReadAsync(id, TimeSpan.FromSeconds(1)));
        Assert.Equal(0, source.Subscribers);
    }

    private sealed class FakeSource : IParcelInfoSource
    {
        private EventHandler<ParcelInfoReplyEventArgs>? _reply;
        public int Subscribers { get; private set; }
        public Action<UUID>? OnRequest { get; set; }
        public event EventHandler<ParcelInfoReplyEventArgs> Reply
        {
            add { _reply += value; Subscribers++; }
            remove { _reply -= value; Subscribers--; }
        }
        public void Request(UUID parcelId) => OnRequest?.Invoke(parcelId);
        public void Deliver(UUID id, float dwell) => _reply?.Invoke(this,
            new ParcelInfoReplyEventArgs(new ParcelInfo
            {
                ID = id, Dwell = dwell, ActualArea = 512,
                SimName = "Example Region", Name = "Example Parcel"
            }));
    }
}

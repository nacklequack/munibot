using OpenMetaverse;

namespace Munibot;

public sealed record ParcelTrafficDto(
    string ParcelId,
    string RegionName,
    string ParcelName,
    int AreaSquareMeters,
    double Dwell,
    DateTimeOffset ObservedAt,
    string Source = "ParcelInfoReply");

public interface IParcelInfoSource
{
    event EventHandler<ParcelInfoReplyEventArgs> Reply;
    void Request(UUID parcelId);
}

internal sealed class GridParcelInfoSource(GridClient client) : IParcelInfoSource
{
    public event EventHandler<ParcelInfoReplyEventArgs> Reply
    {
        add => client.Parcels.ParcelInfoReply += value;
        remove => client.Parcels.ParcelInfoReply -= value;
    }

    public void Request(UUID parcelId) => client.Parcels.RequestParcelInfo(parcelId);
}

public sealed class ParcelTrafficReader(IParcelInfoSource source, TimeProvider? timeProvider = null)
{
    public async Task<ParcelTrafficDto> ReadAsync(
        string parcelId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (!UUID.TryParse(parcelId, out var id) || id == UUID.Zero)
            throw new ArgumentException("A non-zero parcel UUID is required.", nameof(parcelId));

        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<ParcelTrafficDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnReply(object? sender, ParcelInfoReplyEventArgs args)
        {
            var parcel = args.Parcel;
            if (parcel.ID != id)
                return;

            if (!float.IsFinite(parcel.Dwell) || parcel.Dwell < 0 || parcel.ActualArea <= 0
                || string.IsNullOrWhiteSpace(parcel.SimName))
            {
                completion.TrySetException(new InvalidDataException("Second Life returned invalid parcel traffic data."));
                return;
            }

            completion.TrySetResult(new ParcelTrafficDto(
                parcel.ID.ToString(), parcel.SimName, parcel.Name, parcel.ActualArea,
                parcel.Dwell, (timeProvider ?? TimeProvider.System).GetUtcNow()));
        }

        source.Reply += OnReply;
        try
        {
            source.Request(id);
            // Receipt time is not the calculation time or the day represented by this score.
            return await completion.Task.WaitAsync(timeout, cancellationToken);
        }
        finally
        {
            source.Reply -= OnReply;
        }
    }
}

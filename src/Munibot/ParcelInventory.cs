using OpenMetaverse;

namespace Munibot;

public sealed record ParcelInventoryEntryDto(string ParcelId, string Name, int AreaSquareMeters);
public sealed record RegionParcelInventoryDto(
    string RegionId, string RegionName, int AreaSquareMeters, bool Complete,
    DateTimeOffset ObservedAt, IReadOnlyList<ParcelInventoryEntryDto> Parcels);

public sealed partial class SecondLifeBotSession
{
    public async Task<RegionParcelInventoryDto> GetRegionParcelInventoryAsync(
        string regionName, CancellationToken cancellationToken)
    {
        var name = TeleportRequestValidator.NormalizeRegionName(regionName);
        if (!IsOnline)
            throw new InvalidOperationException("Munibot is not logged in.");

        // Inventory discovery must not race bot travel. It never moves the bot itself.
        await _teleportLock.WaitAsync(cancellationToken);
        try
        {
            Simulator? simulator;
            lock (_client.Network.Simulators)
                simulator = _client.Network.Simulators.FirstOrDefault(s =>
                    string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (simulator is null)
                throw new InvalidOperationException("Parcel inventory requires a connection to the requested region. Known parcel UUIDs can be queried remotely.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await _client.Parcels.RequestAllSimParcelsAsync(
                    simulator, true, TimeSpan.FromMilliseconds(250), timeout.Token);

                var positions = new Dictionary<int, Vector3>();
                var map = simulator.ParcelMap;
                for (var y = 0; y < map.GetLength(0); y++)
                    for (var x = 0; x < map.GetLength(1); x++)
                        if (map[y, x] != 0)
                            positions.TryAdd(map[y, x], new Vector3(x * 4 + 2, y * 4 + 2, 0));

                var entries = new Dictionary<string, ParcelInventoryEntryDto>(StringComparer.OrdinalIgnoreCase);
                foreach (var (localId, position) in positions)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    var id = await _client.Parcels.RequestRemoteParcelIDAsync(
                        position, simulator.Handle, simulator.RegionID, timeout.Token);
                    if (id == UUID.Zero)
                        continue;
                    if (simulator.Parcels.TryGetValue(localId, out var parcel))
                        entries[id.ToString()] = new(id.ToString(), parcel.Name, parcel.Area);
                }

                var regionArea = checked((int)(simulator.SizeX * simulator.SizeY));
                var complete = simulator.IsParcelMapFull() && positions.Count == entries.Count
                    && regionArea > 0 && entries.Values.Sum(p => p.AreaSquareMeters) == regionArea;
                return new RegionParcelInventoryDto(simulator.RegionID.ToString(), simulator.Name,
                    regionArea, complete, DateTimeOffset.UtcNow, entries.Values.OrderBy(p => p.ParcelId).ToArray());
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Timed out discovering region parcels.");
            }
        }
        finally
        {
            _teleportLock.Release();
        }
    }
}

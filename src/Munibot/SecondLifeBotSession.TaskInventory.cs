using OpenMetaverse;

namespace Munibot;

public sealed partial class SecondLifeBotSession
{
    public Task<TaskInventoryInspectResultDto> InspectTaskInventoryAsync(string objectUuid,
        TaskInventoryInspectRequestDto request, CancellationToken ct)
    {
        if (request.Names is null || request.Names.Count is < 1 or > 64)
            throw new ArgumentException("Between one and 64 inventory names are required.");
        foreach (var name in request.Names) TaskInventoryContent.ValidateName(name);
        if (request.Names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Names.Count)
            throw new ArgumentException("Inventory names must be unique.");
        return InTaskInventoryAsync(objectUuid, request.Region, request.Position, async (target, token) =>
            new TaskInventoryInspectResultDto(objectUuid, await target.InspectAsync(request.Names, token)), ct);
    }

    public Task<TaskInventoryWriteResultDto> WriteTaskInventoryAsync(string objectUuid, string inventoryName,
        TaskInventoryWriteRequestDto request, CancellationToken ct)
    {
        TaskInventoryContent.ValidateWrite(inventoryName, request);
        return InTaskInventoryAsync(objectUuid, request.Region, request.Position,
            (target, token) => new TaskInventoryWriter(target).WriteAsync(inventoryName, request, token), ct);
    }

    private async Task<T> InTaskInventoryAsync<T>(string objectUuid, string region, Vector3Dto position,
        Func<ITaskInventoryTarget, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        if (!UUID.TryParse(objectUuid, out var objectId) || objectId == UUID.Zero)
            throw new ArgumentException("Object UUID must be a nonzero UUID.");
        var regionName = TeleportRequestValidator.NormalizeRegionName(region);
        var destination = TeleportRequestValidator.NormalizePosition(position);
        if (!IsOnline) throw new TaskInventoryException("bot_offline", "Munibot is not logged in.", true);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(config.Api.TaskInventoryTimeoutSeconds));
        var token = timeout.Token;
        await _teleportLock.WaitAsync(token);
        try
        {
            await _inventoryLock.WaitAsync(token);
            try
            {
                if (!IsCurrentSimulator(regionName) || !IsNearCurrentPosition(destination, 8))
                {
                    // Keep the region lock until the actual teleport completes, even if its caller disconnects.
                    var teleported = await Task.Run(() => _client.Self.Teleport(regionName, destination));
                    token.ThrowIfCancellationRequested();
                    if (!teleported || !IsCurrentSimulator(regionName))
                        throw new TaskInventoryException("target_unreachable", "The target region could not be reached.", true);
                }
                var simulator = _client.Network.CurrentSim
                    ?? throw new TaskInventoryException("bot_offline", "The simulator connection was lost.", true);
                Primitive? primitive = null;
                for (var attempt = 0; attempt < 40 && primitive is null; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    primitive = FindPrimitive(simulator, objectId);
                    if (primitive is null) await Task.Delay(250, token);
                }
                if (primitive is null)
                    throw new TaskInventoryException("target_unreachable", "The exact target object is not visible in the target region.", true);
                if (primitive.IsAttachment)
                    throw new TaskInventoryException("attachment_unsupported", "Task inventory delivery supports rezzed objects only.");
                if ((primitive.Flags & PrimFlags.ObjectModify) == 0)
                    throw new TaskInventoryException("permission_denied", "Munibot does not have modify permission on the target object.");
                return await action(new GridTaskInventoryTarget(_client, simulator, primitive), token);
            }
            finally { _inventoryLock.Release(); }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TaskInventoryException("inventory_timeout", "Task inventory timed out. Inspect the target before retrying.", true, true);
        }
        finally { _teleportLock.Release(); }
    }
}

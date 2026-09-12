using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace Munibot;

public sealed class TaskInventorySharing(
    Func<UUID, OSDMap, CancellationToken, Task<bool>> update,
    Func<UUID, UUID, CancellationToken, Task<InventoryItem?>> fetch)
{
    public const PermissionMask SharedSourceMask = PermissionMask.Move | PermissionMask.Modify | PermissionMask.Copy;

    public static UUID SelectGroup(UUID botId, UUID activeGroup, Primitive.ObjectProperties target)
    {
        if (target.GroupID != UUID.Zero && target.GroupID == activeGroup &&
            (target.Permissions.GroupMask & PermissionMask.Modify) != 0)
            return target.GroupID;
        if (target.OwnerID == botId) return UUID.Zero;
        throw new TaskInventoryException("source_sharing_required",
            "New source in another owner's object requires a group-shared target and that group active on the bot. No inventory was created.");
    }

    public async Task<InventoryItem> PrepareAsync(InventoryItem temporary, UUID botId, UUID groupId, CancellationToken ct)
    {
        if (temporary.OwnerID != botId || temporary.GroupOwned || temporary.UUID == UUID.Zero)
            throw new TaskInventoryException("temporary_owner_mismatch", "Only the temporary bot-owned source may have its sharing prepared.");
        if (groupId == UUID.Zero) return temporary;
        if ((temporary.Permissions.OwnerMask & SharedSourceMask) != SharedSourceMask ||
            (temporary.Permissions.BaseMask & SharedSourceMask) != SharedSourceMask)
            throw new TaskInventoryException("source_sharing_denied", "The temporary source does not permit group sharing.");

        var groupMask = temporary.Permissions.GroupMask | SharedSourceMask;
        // SDK fetches may replace or mutate a cached InventoryItem. Compare against a value snapshot.
        var before = (temporary.UUID, temporary.AssetUUID, temporary.AssetType, temporary.Name,
            temporary.Permissions.BaseMask, temporary.Permissions.OwnerMask,
            temporary.Permissions.EveryoneMask, temporary.Permissions.NextOwnerMask);
        // Patch only group sharing. In particular, do not rewrite ownership, everyone, or next-owner permissions.
        var changes = new OSDMap
        {
            ["permissions"] = new OSDMap
            {
                ["group_id"] = OSD.FromUUID(groupId),
                // Inventory masks are LLSD integers; FromUInteger emits binary data.
                ["group_mask"] = OSD.FromInteger((uint)groupMask)
            }
        };
        if (!await update(before.UUID, changes, ct))
            throw new TaskInventoryException("source_sharing_denied", "The temporary source sharing update was rejected. Nothing was copied to the target.");

        var prepared = await fetch(before.UUID, botId, ct);
        ct.ThrowIfCancellationRequested();
        if (prepared is null || prepared.UUID != before.UUID || prepared.OwnerID != botId || prepared.GroupOwned ||
            prepared.GroupID != groupId || prepared.Permissions.GroupMask != groupMask ||
            prepared.Permissions.BaseMask != before.BaseMask ||
            prepared.Permissions.OwnerMask != before.OwnerMask ||
            prepared.Permissions.EveryoneMask != before.EveryoneMask ||
            prepared.Permissions.NextOwnerMask != before.NextOwnerMask ||
            prepared.AssetUUID != before.AssetUUID || prepared.AssetType != before.AssetType || prepared.Name != before.Name)
            throw new TaskInventoryException("source_sharing_unconfirmed",
                "The temporary source sharing and identity were not verified. Nothing was copied to the target.", true);
        return prepared;
    }

    public static void VerifyCopy(InventoryItem copied, UUID expectedGroup)
    {
        if (expectedGroup != UUID.Zero && (copied.GroupID != expectedGroup ||
            (copied.Permissions.GroupMask & SharedSourceMask) != SharedSourceMask))
            throw new TaskInventoryException("source_sharing_unconfirmed",
                "The new task item did not retain group sharing. Inspect this copy before retrying.", false, true);
    }
}

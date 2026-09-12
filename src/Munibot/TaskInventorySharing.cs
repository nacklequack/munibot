using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace Munibot;

public sealed class TaskInventorySharing(
    Func<UUID, OSDMap, CancellationToken, Task<bool>> update,
    Func<UUID, UUID, CancellationToken, Task<InventoryItem?>> fetch,
    Func<InventoryItem, CancellationToken, Task<UUID>> resolveAssetId)
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
                // Inventory masks are LLSD integers; FromUInteger emits binary data.
                ["group_mask"] = OSD.FromInteger((uint)groupMask)
            }
        };
        if (!await update(before.UUID, changes, ct))
            throw new TaskInventoryException("source_sharing_denied", "The temporary source sharing update was rejected. Nothing was copied to the target.");

        var fetched = await fetch(before.UUID, botId, ct);
        ct.ThrowIfCancellationRequested();
        // Keep SDK cache updates from changing verified metadata while source retrieval is pending.
        var prepared = fetched is null ? null : Copy(fetched);
        var differences = new List<string>();
        if (prepared is null) differences.Add("item_missing");
        else
        {
            CompareId(differences, "item_id", before.UUID, prepared.UUID);
            CompareId(differences, "owner_id", botId, prepared.OwnerID);
            if (prepared.GroupOwned) differences.Add("group_owned (expected false, observed true)");
            // Agent inventory may have sharing enabled without being assigned to a group.
            // The verified destination group is applied to the copy sent to task inventory.
            if (prepared.GroupID != UUID.Zero) CompareId(differences, "group_id", groupId, prepared.GroupID);
            CompareMask(differences, "group_mask", groupMask, prepared.Permissions.GroupMask);
            CompareMask(differences, "base_mask", before.BaseMask, prepared.Permissions.BaseMask);
            CompareMask(differences, "owner_mask", before.OwnerMask, prepared.Permissions.OwnerMask);
            CompareMask(differences, "everyone_mask", before.EveryoneMask, prepared.Permissions.EveryoneMask);
            CompareMask(differences, "next_owner_mask", before.NextOwnerMask, prepared.Permissions.NextOwnerMask);
            if (prepared.AssetUUID != UUID.Zero) CompareId(differences, "asset_id", before.AssetUUID, prepared.AssetUUID);
            if (prepared.AssetType != before.AssetType) differences.Add("asset_type");
            if (prepared.Name != before.Name) differences.Add("name");
        }
        if (differences.Count != 0)
            throw new TaskInventoryException("source_sharing_unconfirmed",
                $"The temporary source sharing and identity were not verified: {string.Join("; ", differences)}. Nothing was copied to the target.", true);

        var resolved = await resolveAssetId(prepared!, ct);
        ct.ThrowIfCancellationRequested();
        if (resolved == UUID.Zero || resolved != before.AssetUUID)
            throw new TaskInventoryException("source_sharing_unconfirmed",
                "The temporary source asset_id did not match the completed upload after source retrieval. Nothing was copied to the target.", true);
        prepared!.AssetUUID = resolved;
        prepared.GroupID = groupId;
        return prepared;
    }

    public static void VerifyCopy(InventoryItem copied, UUID expectedGroup)
    {
        if (expectedGroup == UUID.Zero) return;
        var differences = new List<string>();
        CompareId(differences, "group_id", expectedGroup, copied.GroupID);
        var missing = SharedSourceMask & ~copied.Permissions.GroupMask;
        if (missing != PermissionMask.None)
            differences.Add($"group_mask (required {Mask(SharedSourceMask)}, observed {Mask(copied.Permissions.GroupMask)}, missing {Mask(missing)})");
        if (differences.Count != 0)
            throw new TaskInventoryException("source_sharing_unconfirmed",
                $"The new task item did not retain group sharing: {string.Join("; ", differences)}. Inspect this copy before retrying.", false, true);
    }

    // Identify mismatches without disclosing inventory names, source, or private UUIDs.
    private static void CompareId(List<string> differences, string field, UUID expected, UUID observed)
    {
        if (expected != observed)
            differences.Add($"{field} (expected {(expected == UUID.Zero ? "zero" : "nonzero")}, observed {(observed == UUID.Zero ? "zero" : "nonzero")})");
    }

    private static void CompareMask(List<string> differences, string field, PermissionMask expected, PermissionMask observed)
    {
        if (expected != observed) differences.Add($"{field} (expected {Mask(expected)}, observed {Mask(observed)})");
    }

    private static string Mask(PermissionMask mask) => $"0x{(uint)mask:x8}";

    private static InventoryItem Copy(InventoryItem item) => new(item.InventoryType, item.UUID)
    {
        ParentUUID = item.ParentUUID, Name = item.Name, OwnerID = item.OwnerID,
        AssetUUID = item.AssetUUID, Permissions = item.Permissions, AssetType = item.AssetType,
        CreatorID = item.CreatorID, Description = item.Description, GroupID = item.GroupID,
        GroupOwned = item.GroupOwned, SalePrice = item.SalePrice, SaleType = item.SaleType,
        Flags = item.Flags, CreationDate = item.CreationDate, TransactionID = item.TransactionID,
        LastOwnerID = item.LastOwnerID
    };
}

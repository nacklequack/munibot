using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace Munibot;

internal sealed class ArtifactSourceSharing(
    Func<UUID, OSDMap, CancellationToken, Task<bool>> update,
    Func<UUID, UUID, CancellationToken, Task<InventoryItem?>> fetch)
{
    public async Task<InventoryItem> PrepareAsync(InventoryItem source, UUID botId, UUID groupId,
        CancellationToken ct)
    {
        if (source.UUID == UUID.Zero || source.OwnerID != botId || source.GroupOwned)
            throw new ArtifactRelayException("source_changed",
                "Only the exact received bot-owned artifact may have its sharing prepared.");
        if (groupId == UUID.Zero) return Copy(source);
        if ((source.Permissions.OwnerMask & TaskInventorySharing.SharedSourceMask) !=
                TaskInventorySharing.SharedSourceMask ||
            (source.Permissions.BaseMask & TaskInventorySharing.SharedSourceMask) !=
                TaskInventorySharing.SharedSourceMask)
            throw new ArtifactRelayException("source_permission_denied",
                "The received artifact does not permit the sharing required by the managed scanner.");

        var expectedGroupMask = source.Permissions.GroupMask | TaskInventorySharing.SharedSourceMask;
        var before = Snapshot.Capture(source);
        if (source.Permissions.GroupMask != expectedGroupMask)
        {
            var changes = new OSDMap
            {
                ["permissions"] = new OSDMap
                {
                    // Inventory masks are LLSD integers; FromUInteger emits binary data.
                    ["group_mask"] = OSD.FromInteger((uint)expectedGroupMask)
                }
            };
            if (!await update(before.ItemId, changes, ct))
                throw new ArtifactRelayException("source_sharing_denied",
                    "The received artifact sharing update was rejected. Nothing was copied to the target.");
        }

        var fetched = await fetch(before.ItemId, botId, ct);
        ct.ThrowIfCancellationRequested();
        // SDK fetches may replace or mutate a cached InventoryItem. Verify a detached value copy.
        var prepared = fetched is null ? null : Copy(fetched);
        var differences = new List<string>();
        if (prepared is null) differences.Add("item_missing");
        else
        {
            CompareId(differences, "item_id", before.ItemId, prepared.UUID);
            CompareId(differences, "owner_id", botId, prepared.OwnerID);
            if (prepared.GroupOwned) differences.Add("group_owned (expected false, observed true)");
            // Agent inventory may retain no group assignment after its sharing mask is enabled.
            if (prepared.GroupID != UUID.Zero) CompareId(differences, "group_id", groupId, prepared.GroupID);
            CompareMask(differences, "group_mask", expectedGroupMask, prepared.Permissions.GroupMask);
            CompareMask(differences, "base_mask", before.BaseMask, prepared.Permissions.BaseMask);
            CompareMask(differences, "owner_mask", before.OwnerMask, prepared.Permissions.OwnerMask);
            CompareMask(differences, "everyone_mask", before.EveryoneMask, prepared.Permissions.EveryoneMask);
            CompareMask(differences, "next_owner_mask", before.NextOwnerMask, prepared.Permissions.NextOwnerMask);
            // Object AssetUUID can remain opaque. A visible identity must not change, while an
            // opaque-to-visible transition is safe because the item UUID remains receipt-bound.
            if (before.AssetId != UUID.Zero && prepared.AssetUUID != UUID.Zero)
                CompareId(differences, "asset_id", before.AssetId, prepared.AssetUUID);
            if (prepared.AssetType != before.AssetType) differences.Add("asset_type");
            if (prepared.InventoryType != before.InventoryType) differences.Add("inventory_type");
            if (prepared.Name != before.Name) differences.Add("name");
            if (prepared.Description != before.Description) differences.Add("description");
        }
        if (differences.Count != 0)
            throw new ArtifactRelayException("source_sharing_unconfirmed",
                $"The received artifact sharing and identity were not verified: {string.Join("; ", differences)}. Nothing was copied to the target.",
                true);

        if (prepared!.AssetUUID == UUID.Zero) prepared.AssetUUID = before.AssetId;
        return prepared;
    }

    // Identify mismatches without disclosing inventory names or private UUIDs.
    private static void CompareId(List<string> differences, string field, UUID expected, UUID observed)
    {
        if (expected != observed)
            differences.Add($"{field} (expected {(expected == UUID.Zero ? "zero" : "nonzero")}, observed {(observed == UUID.Zero ? "zero" : "nonzero")})");
    }

    private static void CompareMask(List<string> differences, string field, PermissionMask expected,
        PermissionMask observed)
    {
        if (expected != observed) differences.Add($"{field} (expected {Mask(expected)}, observed {Mask(observed)})");
    }

    private static string Mask(PermissionMask mask) => $"0x{(uint)mask:x8}";

    private static InventoryItem Copy(InventoryItem item) => new(item.InventoryType, item.UUID)
    {
        ParentUUID = item.ParentUUID,
        Name = item.Name,
        OwnerID = item.OwnerID,
        AssetUUID = item.AssetUUID,
        Permissions = item.Permissions,
        AssetType = item.AssetType,
        CreatorID = item.CreatorID,
        Description = item.Description,
        GroupID = item.GroupID,
        GroupOwned = item.GroupOwned,
        SalePrice = item.SalePrice,
        SaleType = item.SaleType,
        Flags = item.Flags,
        CreationDate = item.CreationDate,
        TransactionID = item.TransactionID,
        LastOwnerID = item.LastOwnerID
    };

    private sealed record Snapshot(
        UUID ItemId,
        UUID AssetId,
        AssetType AssetType,
        InventoryType InventoryType,
        string Name,
        string Description,
        PermissionMask BaseMask,
        PermissionMask OwnerMask,
        PermissionMask EveryoneMask,
        PermissionMask NextOwnerMask)
    {
        internal static Snapshot Capture(InventoryItem item) => new(
            item.UUID, item.AssetUUID, item.AssetType, item.InventoryType, item.Name, item.Description,
            item.Permissions.BaseMask, item.Permissions.OwnerMask, item.Permissions.EveryoneMask,
            item.Permissions.NextOwnerMask);
    }
}

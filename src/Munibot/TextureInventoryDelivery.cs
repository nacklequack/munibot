using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace Munibot;

public sealed record FullPermissionTextureRequestDto(string AvatarId, string AssetId, string ItemId);
public sealed record FullPermissionTextureResultDto(bool Success, bool FullPermissionsVerified);

public sealed class TextureInventoryDelivery(
    Func<UUID, UUID, CancellationToken, Task<InventoryItem?>> fetch,
    Func<UUID, OSDMap, CancellationToken, Task<bool>> update,
    Action<UUID, string, AssetType, UUID> give)
{
    public const PermissionMask Required = PermissionMask.Copy | PermissionMask.Modify | PermissionMask.Transfer;

    public async Task SendAsync(UUID itemId, UUID assetId, UUID botId, UUID recipient, CancellationToken ct)
    {
        var item = await fetch(itemId, botId, ct)
            ?? throw new KeyNotFoundException("The minted texture inventory item was not found.");
        Verify(item, itemId, assetId, botId);
        var before = (item.Permissions.BaseMask, item.Permissions.OwnerMask, item.Permissions.GroupMask,
            item.Permissions.EveryoneMask, item.Permissions.NextOwnerMask);
        var next = before.NextOwnerMask | Required;
        if (next != before.NextOwnerMask)
        {
            var changes = new OSDMap
            {
                ["permissions"] = new OSDMap { ["next_owner_mask"] = OSD.FromInteger((uint)next) }
            };
            if (!await update(itemId, changes, ct))
                throw new InvalidOperationException("The texture permissions could not be prepared. Nothing was sent.");
            item = await fetch(itemId, botId, ct)
                ?? throw new InvalidOperationException("The texture permissions could not be verified. Nothing was sent.");
            Verify(item, itemId, assetId, botId);
            if (item.Permissions.NextOwnerMask != next || item.Permissions.BaseMask != before.BaseMask
                || item.Permissions.OwnerMask != before.OwnerMask || item.Permissions.GroupMask != before.GroupMask
                || item.Permissions.EveryoneMask != before.EveryoneMask)
                throw new InvalidOperationException("The texture permission readback did not match. Nothing was sent.");
        }
        ct.ThrowIfCancellationRequested();
        give(itemId, item.Name, AssetType.Texture, recipient);
    }

    private static void Verify(InventoryItem item, UUID itemId, UUID assetId, UUID botId)
    {
        if (item.UUID != itemId || item.AssetUUID != assetId || item.AssetType != AssetType.Texture
            || item.OwnerID != botId || item.GroupOwned)
            throw new InvalidOperationException("The inventory item does not match the minted texture. Nothing was sent.");
        if ((item.Permissions.BaseMask & Required) != Required || (item.Permissions.OwnerMask & Required) != Required)
            throw new InvalidOperationException("The source texture does not allow full permissions. Nothing was sent.");
    }
}

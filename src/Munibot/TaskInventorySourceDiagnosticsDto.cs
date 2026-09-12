using OpenMetaverse;

namespace Munibot;

public sealed record TaskInventorySourceDiagnosticsDto(
    string BotId, string ActiveGroupId, string ObjectId, string ItemId, string RequestedAssetId,
    string ItemOwnerId, string ItemGroupId, bool ItemGroupOwned, string AssetType,
    string BaseMask, string OwnerMask, string GroupMask, string EveryoneMask, string NextOwnerMask,
    string? TransferStatus = null, bool? TransferSucceeded = null)
{
    // These are reported inventory masks, not a claim about the bot's effective access.
    public static TaskInventorySourceDiagnosticsDto Capture(UUID botId, UUID activeGroupId, UUID objectId, InventoryItem item)
        => new(botId.ToString(), activeGroupId.ToString(), objectId.ToString(), item.UUID.ToString(), item.AssetUUID.ToString(),
            item.OwnerID.ToString(), item.GroupID.ToString(), item.GroupOwned, item.AssetType.ToString(),
            Mask(item.Permissions.BaseMask), Mask(item.Permissions.OwnerMask), Mask(item.Permissions.GroupMask),
            Mask(item.Permissions.EveryoneMask), Mask(item.Permissions.NextOwnerMask));

    private static string Mask(PermissionMask value) => ((uint)value).ToString("x8");
}

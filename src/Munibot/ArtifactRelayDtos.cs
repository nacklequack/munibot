using OpenMetaverse;

namespace Munibot;

public sealed record InventoryPermissionMasksDto(
    uint Base,
    uint Owner,
    uint Group,
    uint Everyone,
    uint NextOwner)
{
    internal static InventoryPermissionMasksDto From(Permissions permissions) => new(
        (uint)permissions.BaseMask,
        (uint)permissions.OwnerMask,
        (uint)permissions.GroupMask,
        (uint)permissions.EveryoneMask,
        (uint)permissions.NextOwnerMask);

    internal bool Matches(Permissions permissions) => this == From(permissions);
}

public sealed record ArtifactInventoryItemDto(
    string ItemId,
    string AssetId,
    string Name,
    string AssetType,
    string InventoryType,
    string OwnerId,
    string GroupId,
    bool GroupOwned,
    InventoryPermissionMasksDto Permissions,
    bool CanModify,
    bool CanCopy,
    bool CanTransfer)
{
    internal static ArtifactInventoryItemDto From(InventoryItem item) => new(
        item.UUID.ToString(),
        item.AssetUUID.ToString(),
        item.Name,
        item.AssetType.ToString(),
        item.InventoryType.ToString(),
        item.OwnerID.ToString(),
        item.GroupID.ToString(),
        item.GroupOwned,
        InventoryPermissionMasksDto.From(item.Permissions),
        Has(item, PermissionMask.Modify),
        Has(item, PermissionMask.Copy),
        Has(item, PermissionMask.Transfer));

    private static bool Has(InventoryItem item, PermissionMask mask) =>
        (item.Permissions.OwnerMask & mask) == mask;
}

public sealed record ArtifactOfferExpectationRequestDto(
    string SourceObjectUuid,
    string SourceOwnerUuid,
    string SourceObjectName,
    string InventoryName,
    string AssetId,
    InventoryPermissionMasksDto Permissions);

public sealed record ArtifactOfferReceiptDto(
    string RequestId,
    string SourceObjectUuid,
    DateTimeOffset ReceivedAt,
    ArtifactInventoryItemDto Item);

public sealed record ArtifactOfferStatusDto(
    string RequestId,
    string Status,
    DateTimeOffset ExpiresAt,
    ArtifactOfferReceiptDto? Receipt,
    string? ErrorCode,
    string? Error,
    bool Retryable,
    bool OutcomeUnknown);

public sealed record ArtifactBundleMarkerDto(string Name, string AssetId, string AssetType);

public sealed record ArtifactRelayRequestDto(
    string Region,
    Vector3Dto Position,
    string TargetName,
    string TargetOwnerId,
    string TargetGroupId,
    string ExpectedBundleKey,
    IReadOnlyList<ArtifactBundleMarkerDto> BundleMarkers,
    InventoryPermissionMasksDto ExpectedTargetPermissions);

public sealed record ArtifactRelayResultDto(
    string RequestId,
    string ObjectUuid,
    string ExpectedBundleKey,
    bool Copied,
    bool Idempotent,
    ArtifactInventoryItemDto Source,
    ArtifactInventoryItemDto Target);

public sealed record ArtifactRelayErrorDto(
    string ErrorCode,
    string Error,
    bool Retryable,
    bool OutcomeUnknown);

public sealed class ArtifactRelayException(
    string code,
    string message,
    bool retryable = false,
    bool outcomeUnknown = false) : Exception(message)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
    public bool OutcomeUnknown { get; } = outcomeUnknown;
}

internal sealed record ArtifactOfferExpectation(
    Guid RequestId,
    UUID SourceObjectId,
    UUID SourceOwnerId,
    string SourceObjectName,
    string InventoryName,
    UUID AssetId,
    InventoryPermissionMasksDto Permissions);

internal sealed record ArtifactOfferSignal(
    bool FromTask,
    UUID SourceOwnerId,
    string SourceObjectName,
    string InventoryName,
    AssetType AssetType);

internal sealed record ArtifactOfferDecision(bool Accept, string Reason);

internal sealed record ArtifactRelaySpec(
    UUID TargetObjectId,
    string Region,
    Vector3 Position,
    string TargetName,
    UUID TargetOwnerId,
    UUID TargetGroupId,
    string ExpectedBundleKey,
    IReadOnlyList<ArtifactBundleMarkerSpec> BundleMarkers,
    InventoryPermissionMasksDto ExpectedTargetPermissions);

internal sealed record ArtifactBundleMarkerSpec(string Name, UUID AssetId, AssetType AssetType);

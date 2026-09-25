using OpenMetaverse;

namespace Munibot;

internal static class ArtifactRelayValidator
{
    public static ArtifactOfferExpectation NormalizeExpectation(string requestId,
        ArtifactOfferExpectationRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Guid.TryParse(requestId, out var parsedRequestId) || parsedRequestId == Guid.Empty)
            throw new ArgumentException("Request ID must be a nonzero UUID.");
        var sourceObjectId = Uuid(request.SourceObjectUuid, "Source object UUID");
        var sourceOwnerId = Uuid(request.SourceOwnerUuid, "Source owner UUID");
        var sourceObjectName = ExactName(request.SourceObjectName, "Source object name");
        var inventoryName = ExactName(request.InventoryName, "Inventory name", rejectQuote: true);
        var assetId = Uuid(request.AssetId, "Asset UUID");
        ArgumentNullException.ThrowIfNull(request.Permissions);
        return new(parsedRequestId, sourceObjectId, sourceOwnerId, sourceObjectName, inventoryName,
            assetId, request.Permissions);
    }

    public static ArtifactRelaySpec NormalizeRelay(string objectUuid, ArtifactRelayRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var targetObjectId = Uuid(objectUuid, "Object UUID");
        var region = TeleportRequestValidator.NormalizeRegionName(request.Region);
        var position = TeleportRequestValidator.NormalizePosition(request.Position);
        var targetName = ExactName(request.TargetName, "Target name");
        var targetOwner = Uuid(request.TargetOwnerId, "Target owner UUID");
        var targetGroup = Uuid(request.TargetGroupId, "Target group UUID", allowZero: true);
        var bundleKey = ExactName(request.ExpectedBundleKey, "Expected bundle key");
        ArgumentNullException.ThrowIfNull(request.ExpectedTargetPermissions);
        if (request.BundleMarkers is null || request.BundleMarkers.Count is < 1 or > 16)
            throw new ArgumentException("Between one and 16 exact bundle markers are required.");
        var markers = request.BundleMarkers.Select((marker, index) => NormalizeMarker(marker, index)).ToList();
        if (markers.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != markers.Count)
            throw new ArgumentException("Bundle marker names must be unique and case-sensitive.");
        return new(targetObjectId, region, position, targetName, targetOwner, targetGroup, bundleKey,
            markers, request.ExpectedTargetPermissions);
    }

    private static ArtifactBundleMarkerSpec NormalizeMarker(ArtifactBundleMarkerDto marker, int index)
    {
        ArgumentNullException.ThrowIfNull(marker);
        var name = ExactName(marker.Name, $"Bundle marker {index + 1} name");
        var assetId = Uuid(marker.AssetId, $"Bundle marker {index + 1} asset UUID");
        if (!Enum.TryParse<AssetType>(marker.AssetType, true, out var assetType) || assetType is AssetType.Unknown)
            throw new ArgumentException($"Bundle marker {index + 1} has an unsupported asset type.");
        return new(name, assetId, assetType);
    }

    private static UUID Uuid(string? value, string label, bool allowZero = false)
    {
        if (string.IsNullOrWhiteSpace(value) || !UUID.TryParse(value, out var parsed) ||
            (!allowZero && parsed == UUID.Zero))
            throw new ArgumentException($"{label} must be a{(allowZero ? "" : " nonzero")} UUID.");
        return parsed;
    }

    private static string ExactName(string? value, string label, bool rejectQuote = false)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Length > 255 ||
            value.IndexOfAny(['\r', '\n', '\0']) >= 0 || (rejectQuote && value.Contains('\'')))
            throw new ArgumentException($"{label} must be an exact inventory-safe value of at most 255 characters.");
        return value;
    }
}

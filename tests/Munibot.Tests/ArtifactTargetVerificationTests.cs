using OpenMetaverse;

namespace Munibot.Tests;

public sealed class ArtifactTargetVerificationTests
{
    private static readonly UUID Bot = UUID.Random();
    private static readonly UUID Target = UUID.Random();
    private static readonly UUID Owner = UUID.Random();
    private static readonly UUID Group = UUID.Random();
    private static readonly UUID Asset = UUID.Random();
    private static readonly InventoryPermissionMasksDto TargetMasks = new(
        (uint)PermissionMask.All, (uint)(PermissionMask.Move | PermissionMask.Copy),
        0, 0, (uint)(PermissionMask.Move | PermissionMask.Copy));

    [Theory]
    [InlineData(PermissionMask.Modify)]
    [InlineData(PermissionMask.Copy)]
    [InlineData(PermissionMask.Transfer)]
    public void RejectsSourceWithoutRequiredPermissionBeforeMutation(PermissionMask missing)
    {
        var item = Source();
        item.Permissions.OwnerMask &= ~missing;
        var error = Assert.Throws<ArtifactRelayException>(() =>
            GridTaskInventoryTarget.VerifyArtifactSource(item));
        Assert.Equal("source_permission_denied", error.Code);
        Assert.False(error.OutcomeUnknown);
    }

    [Fact]
    public void PreparesCopyOnlyDeliveryWithoutChangingBotSource()
    {
        var source = Source();
        var original = source.Permissions;

        GridTaskInventoryTarget.VerifyTargetPermissionPolicy(source, TargetMasks);
        var delivery = GridTaskInventoryTarget.PrepareDelivery(source, TargetMasks);

        Assert.Equal(TargetMasks, InventoryPermissionMasksDto.From(delivery.Permissions));
        Assert.Equal(InventoryPermissionMasksDto.From(original),
            InventoryPermissionMasksDto.From(source.Permissions));
        Assert.True((delivery.Permissions.OwnerMask & PermissionMask.Copy) != 0);
        Assert.True((delivery.Permissions.OwnerMask & PermissionMask.Modify) == 0);
        Assert.True((delivery.Permissions.OwnerMask & PermissionMask.Transfer) == 0);
    }

    [Theory]
    [InlineData(PermissionMask.Modify)]
    [InlineData(PermissionMask.Transfer)]
    public void RejectsOverPermissiveScannerTarget(PermissionMask permission)
    {
        var copyOnly = (uint)(PermissionMask.Move | PermissionMask.Copy);
        var requested = TargetMasks with { Owner = copyOnly | (uint)permission };

        var error = Assert.Throws<ArtifactRelayException>(() =>
            GridTaskInventoryTarget.VerifyTargetPermissionPolicy(Source(), requested));

        Assert.Equal("target_permission_policy_invalid", error.Code);
        Assert.False(error.OutcomeUnknown);
    }

    [Fact]
    public void RejectsAnyTargetIdentityMismatch()
    {
        var properties = Properties();
        var spec = Spec();
        properties.OwnerID = UUID.Random();
        var error = Assert.Throws<ArtifactRelayException>(() =>
            GridTaskInventoryTarget.VerifyTargetIdentity(properties, spec));
        Assert.Equal("target_identity_mismatch", error.Code);
    }

    [Fact]
    public void RejectsWrongBundleMarkerIdentity()
    {
        var marker = Marker("managed-runtime", AssetType.LSLText, UUID.Random());
        var requested = new ArtifactBundleMarkerSpec(marker.Name, UUID.Random(), marker.AssetType);
        var error = Assert.Throws<ArtifactRelayException>(() =>
            GridTaskInventoryTarget.VerifyBundle([marker], [requested]));
        Assert.Equal("bundle_mismatch", error.Code);
    }

    [Fact]
    public void ExistingExactCopyIsSafeIdempotentRetry()
    {
        var source = Source();
        var existing = Source(UUID.Random());
        existing.Permissions = Permissions(TargetMasks);

        var found = GridTaskInventoryTarget.FindExactName([Marker("managed-runtime", AssetType.LSLText), existing], source.Name);
        GridTaskInventoryTarget.VerifyDelivered(found!, source, TargetMasks, false);

        Assert.Same(existing, found);
    }

    [Fact]
    public void ExistingWrongCopyFailsWithoutUnknownOutcome()
    {
        var source = Source();
        var existing = Source(UUID.Random());
        existing.AssetUUID = UUID.Random();
        existing.Permissions = Permissions(TargetMasks);
        var error = Assert.Throws<ArtifactRelayException>(() =>
            GridTaskInventoryTarget.VerifyDelivered(existing, source, TargetMasks, false));
        Assert.Equal("target_receipt_mismatch", error.Code);
        Assert.False(error.OutcomeUnknown);
    }

    [Fact]
    public void PostCopyPermissionMismatchHasUnknownOutcomeAndPreservesPriorItems()
    {
        var source = Source();
        var copied = Source(UUID.Random());
        copied.Permissions = new Permissions(0, 0, 0, 0, 0);
        var error = Assert.Throws<ArtifactRelayException>(() =>
            GridTaskInventoryTarget.VerifyDelivered(copied, source, TargetMasks, true));
        Assert.Equal("target_receipt_mismatch", error.Code);
        Assert.True(error.OutcomeUnknown);
    }

    [Fact]
    public void CaseVariantOrDuplicateArtifactNamesAreConflicts()
    {
        var error = Assert.Throws<ArtifactRelayException>(() =>
            GridTaskInventoryTarget.FindExactName([Source(), Source(UUID.Random())], "Scanner HUD"));
        Assert.Equal("target_inventory_conflict", error.Code);
        Assert.False(error.OutcomeUnknown);
    }

    private static ArtifactRelaySpec Spec() => new(Target, "Briarmont", new Vector3(128, 128, 25),
        "HUD Scanner", Owner, Group, "hud-runtime-scanner",
        [new ArtifactBundleMarkerSpec("managed-runtime", UUID.Random(), AssetType.LSLText)], TargetMasks);

    private static Primitive.ObjectProperties Properties() => new()
    {
        ObjectID = Target,
        Name = "HUD Scanner",
        OwnerID = Owner,
        GroupID = Group
    };

    private static InventoryItem Source(UUID? itemId = null) => new(InventoryType.Object, itemId ?? UUID.Random())
    {
        Name = "Scanner HUD",
        AssetUUID = Asset,
        AssetType = AssetType.Object,
        InventoryType = InventoryType.Object,
        OwnerID = Bot,
        Permissions = new Permissions((uint)PermissionMask.All, 0,
            (uint)TaskInventorySharing.SharedSourceMask,
            (uint)(PermissionMask.Move | PermissionMask.Copy | PermissionMask.Transfer),
            (uint)PermissionMask.All)
    };

    private static InventoryItem Marker(string name, AssetType type, UUID? asset = null) =>
        new(type == AssetType.LSLText ? InventoryType.LSL : InventoryType.Object, UUID.Random())
        { Name = name, AssetType = type, AssetUUID = asset ?? UUID.Random() };

    private static Permissions Permissions(InventoryPermissionMasksDto masks) =>
        new(masks.Base, masks.Everyone, masks.Group, masks.NextOwner, masks.Owner);
}

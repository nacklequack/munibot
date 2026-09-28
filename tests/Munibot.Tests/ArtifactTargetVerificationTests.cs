using OpenMetaverse;
using System.Text;

namespace Munibot.Tests;

public sealed class ArtifactTargetVerificationTests
{
    private const string DeliveryMarker = "munibase-artifact:11111111111141118111111111111111";
    private static readonly UUID Bot = UUID.Random();
    private static readonly UUID Target = UUID.Random();
    private static readonly UUID Owner = UUID.Random();
    private static readonly UUID Group = UUID.Random();
    private static readonly UUID Asset = UUID.Random();
    private static readonly InventoryPermissionMasksDto TargetMasks = new(
        (uint)PermissionMask.All, (uint)(PermissionMask.Move | PermissionMask.Copy),
        0, 0, (uint)(PermissionMask.Move | PermissionMask.Copy));
    private static readonly InventoryPermissionMasksDto MarkerMasks = new(
        (uint)PermissionMask.All, (uint)PermissionMask.All,
        (uint)PermissionMask.All, (uint)PermissionMask.All, (uint)PermissionMask.All);

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
    public void AcceptsVerifiedObjectInventoryIdentityWhenAssetIdentityIsHidden()
    {
        var source = Source();
        source.AssetUUID = UUID.Zero;

        GridTaskInventoryTarget.VerifyArtifactSource(source);
        var delivery = GridTaskInventoryTarget.PrepareDelivery(source, DeliveryMarker, TargetMasks);

        Assert.Equal(source.UUID, delivery.UUID);
        Assert.Equal(UUID.Zero, delivery.AssetUUID);
        Assert.Equal(DeliveryMarker, delivery.Description);
    }

    [Fact]
    public void KeepsBotTransferPermissionUntilCopyOnlyOwnershipTransition()
    {
        var source = Source();
        var original = source.Permissions;

        GridTaskInventoryTarget.VerifyTargetPermissionPolicy(source, TargetMasks);
        var delivery = GridTaskInventoryTarget.PrepareDelivery(source, DeliveryMarker, TargetMasks);

        Assert.Equal(InventoryPermissionMasksDto.From(original),
            InventoryPermissionMasksDto.From(source.Permissions));
        Assert.Equal(DeliveryMarker, delivery.Description);
        Assert.Equal(source.Permissions.OwnerMask, delivery.Permissions.OwnerMask);
        Assert.Equal((PermissionMask)TargetMasks.Owner, delivery.Permissions.NextOwnerMask);
        Assert.True((delivery.Permissions.OwnerMask & PermissionMask.Copy) != 0);
        Assert.True((delivery.Permissions.OwnerMask & PermissionMask.Modify) != 0);
        Assert.True((delivery.Permissions.OwnerMask & PermissionMask.Transfer) != 0);
        Assert.True((delivery.Permissions.NextOwnerMask & PermissionMask.Copy) != 0);
        Assert.True((delivery.Permissions.NextOwnerMask & PermissionMask.Modify) == 0);
        Assert.True((delivery.Permissions.NextOwnerMask & PermissionMask.Transfer) == 0);
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

        var match = GridTaskInventoryTarget.MatchBundleMarker([marker], requested);

        Assert.Contains("asset_identity", match.Mismatches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompleteScriptProofAllowsNonComparableOrOpaqueAssetIdentity(bool opaque)
    {
        var marker = Marker("titler-scanner.lsl", AssetType.LSLText, UUID.Random());
        if (opaque) marker.AssetUUID = UUID.Zero;
        marker.Permissions = Permissions(MarkerMasks);
        var source = Encoding.UTF8.GetBytes("default { state_entry() { } }\n");
        var proof = new ArtifactBundleScriptProofSpec(InventoryType.LSL, MarkerMasks, true,
            TaskInventoryContent.Hash(source), Guid.NewGuid());
        var requested = new ArtifactBundleMarkerSpec(marker.Name, UUID.Random(), marker.AssetType, proof);

        var match = GridTaskInventoryTarget.MatchBundleMarker([marker], requested);
        var evidence = GridTaskInventoryTarget.ScriptProofMismatches(
            proof, source, true, proof.ExperienceId);

        Assert.NotEqual(marker.AssetUUID, requested.AssetId);
        Assert.Empty(match.Mismatches);
        Assert.Empty(evidence);
    }

    [Theory]
    [InlineData("inventory-type", "inventory_type")]
    [InlineData("permissions", "permissions")]
    public void ScriptProofRequiresExactStructuralEvidence(string mismatch, string field)
    {
        var marker = Marker("titler-scanner.lsl", AssetType.LSLText);
        marker.Permissions = Permissions(MarkerMasks);
        var proof = ScriptProof();
        if (mismatch == "inventory-type") marker.InventoryType = InventoryType.Notecard;
        if (mismatch == "permissions") marker.Permissions.OwnerMask &= ~PermissionMask.Transfer;

        var match = GridTaskInventoryTarget.MatchBundleMarker([marker],
            new(marker.Name, UUID.Random(), AssetType.LSLText, proof));

        Assert.Contains(field, match.Mismatches);
    }

    [Theory]
    [InlineData("hash", "source_hash")]
    [InlineData("running", "running")]
    [InlineData("experience", "experience")]
    [InlineData("experience-unconfirmed", "experience_unconfirmed")]
    public void ScriptProofRejectsAnyRuntimeEvidenceMismatch(string mismatch, string field)
    {
        var proof = ScriptProof();
        var source = Encoding.UTF8.GetBytes("default { state_entry() { } }\n");
        var running = proof.Running;
        Guid? experience = proof.ExperienceId;
        if (mismatch == "running") running = !running;
        if (mismatch == "experience") experience = Guid.NewGuid();
        if (mismatch == "experience-unconfirmed") experience = null;

        var mismatches = GridTaskInventoryTarget.ScriptProofMismatches(
            proof, mismatch == "hash" ? Encoding.UTF8.GetBytes("changed\n") : source,
            running, experience);

        Assert.Contains(field, mismatches);
    }

    [Fact]
    public void BundleMarkerStillRequiresExactCaseAndSingleInventoryItem()
    {
        var marker = Marker("titler-scanner.lsl", AssetType.LSLText);
        var requested = new ArtifactBundleMarkerSpec(marker.Name, marker.AssetUUID, marker.AssetType);
        var caseVariant = Marker("Titler-Scanner.lsl", AssetType.LSLText, marker.AssetUUID);

        Assert.Contains("inventory_name",
            GridTaskInventoryTarget.MatchBundleMarker([caseVariant], requested).Mismatches);
        Assert.Contains("inventory_count",
            GridTaskInventoryTarget.MatchBundleMarker([marker, Marker(marker.Name, marker.AssetType)], requested)
                .Mismatches);
    }

    [Fact]
    public void ExistingExactCopyIsSafeIdempotentRetry()
    {
        var source = Source();
        var existing = Source(UUID.Random());
        existing.Description = DeliveryMarker;
        existing.Permissions = Permissions(TargetMasks);

        var found = GridTaskInventoryTarget.FindExactName([Marker("managed-runtime", AssetType.LSLText), existing], source.Name);
        GridTaskInventoryTarget.VerifyDelivered(found!, source, DeliveryMarker, TargetMasks, false);

        Assert.Same(existing, found);
    }

    [Fact]
    public void ExistingWrongMarkerFailsWithoutUnknownOutcome()
    {
        var source = Source();
        var existing = Source(UUID.Random());
        existing.Description = "munibase-artifact:22222222222242228222222222222222";
        existing.Permissions = Permissions(TargetMasks);
        var error = Assert.Throws<ArtifactRelayException>(() =>
            GridTaskInventoryTarget.VerifyDelivered(existing, source, DeliveryMarker, TargetMasks, false));
        Assert.Equal("target_receipt_mismatch", error.Code);
        Assert.Contains("delivery marker", error.Message);
        Assert.False(error.OutcomeUnknown);
    }

    [Fact]
    public void DestinationMayRekeyTheAssetWhenMarkerAndPermissionsMatch()
    {
        var source = Source();
        var delivered = Source(UUID.Random());
        delivered.AssetUUID = UUID.Random();
        delivered.Description = DeliveryMarker;
        delivered.Permissions = Permissions(TargetMasks);

        GridTaskInventoryTarget.VerifyDelivered(delivered, source, DeliveryMarker, TargetMasks, true);
    }

    [Fact]
    public void CopyOnlyDestinationMayHideAssetIdentityWhenMarkerAndPermissionsMatch()
    {
        var source = Source();
        var delivered = Source(UUID.Random());
        delivered.AssetUUID = UUID.Zero;
        delivered.Description = DeliveryMarker;
        delivered.Permissions = Permissions(TargetMasks);

        GridTaskInventoryTarget.VerifyDelivered(delivered, source, DeliveryMarker, TargetMasks, true);
    }

    [Fact]
    public void PostCopyPermissionMismatchHasUnknownOutcomeAndPreservesPriorItems()
    {
        var source = Source();
        var copied = Source(UUID.Random());
        copied.Description = DeliveryMarker;
        copied.Permissions = new Permissions(0, 0, 0, 0, 0);
        var error = Assert.Throws<ArtifactRelayException>(() =>
            GridTaskInventoryTarget.VerifyDelivered(copied, source, DeliveryMarker, TargetMasks, true));
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
        "HUD Scanner", Owner, Group, "hud-runtime-scanner", DeliveryMarker,
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

    private static ArtifactBundleScriptProofSpec ScriptProof()
    {
        var source = Encoding.UTF8.GetBytes("default { state_entry() { } }\n");
        return new(InventoryType.LSL, MarkerMasks, true, TaskInventoryContent.Hash(source), Guid.NewGuid());
    }

    private static Permissions Permissions(InventoryPermissionMasksDto masks) =>
        new(masks.Base, masks.Everyone, masks.Group, masks.NextOwner, masks.Owner);
}

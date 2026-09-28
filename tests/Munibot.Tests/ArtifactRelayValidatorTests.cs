using OpenMetaverse;

namespace Munibot.Tests;

public sealed class ArtifactRelayValidatorTests
{
    private static readonly InventoryPermissionMasksDto Masks = new(
        (uint)PermissionMask.All,
        (uint)PermissionMask.All,
        (uint)PermissionMask.All,
        (uint)PermissionMask.All,
        (uint)PermissionMask.All);

    [Fact]
    public void ExactAssetMarkerRemainsSupportedWithoutScriptProof()
    {
        var marker = new ArtifactBundleMarkerDto("managed-runtime", Guid.NewGuid().ToString(), "LSLText");

        var spec = ArtifactRelayValidator.NormalizeRelay(Guid.NewGuid().ToString(), Request(marker));

        Assert.Null(Assert.Single(spec.BundleMarkers).ScriptProof);
    }

    [Fact]
    public void CompleteScriptProofIsNormalizedAsExplicitCompatibilityEvidence()
    {
        var experience = Guid.NewGuid();
        var proof = new ArtifactBundleScriptProofDto("LSL", Masks, true, new string('a', 64),
            experience.ToString());
        var marker = new ArtifactBundleMarkerDto("titler-scanner.lsl", Guid.NewGuid().ToString(),
            "LSLText", proof);

        var spec = ArtifactRelayValidator.NormalizeRelay(Guid.NewGuid().ToString(), Request(marker));

        var normalized = Assert.Single(spec.BundleMarkers).ScriptProof!;
        Assert.Equal(InventoryType.LSL, normalized.InventoryType);
        Assert.Equal(Masks, normalized.Permissions);
        Assert.True(normalized.Running);
        Assert.Equal(new string('a', 64), normalized.SourceSha256);
        Assert.Equal(experience, normalized.ExperienceId);
    }

    [Theory]
    [InlineData("asset-type")]
    [InlineData("inventory-type")]
    [InlineData("hash-uppercase")]
    [InlineData("hash-length")]
    [InlineData("experience")]
    [InlineData("stopped")]
    [InlineData("permissions")]
    public void IncompleteOrInvalidCompatibilityProofIsRejected(string mismatch)
    {
        var proof = new ArtifactBundleScriptProofDto(
            mismatch == "inventory-type" ? "Object" : "LSL",
            mismatch == "permissions" ? Masks with { Owner = 0 } : Masks,
            mismatch != "stopped",
            mismatch == "hash-uppercase" ? new string('A', 64) :
            mismatch == "hash-length" ? new string('a', 63) : new string('a', 64),
            mismatch == "experience" ? "invalid" : Guid.NewGuid().ToString());
        var marker = new ArtifactBundleMarkerDto("titler-scanner.lsl", Guid.NewGuid().ToString(),
            mismatch == "asset-type" ? "Notecard" : "LSLText", proof);

        Assert.Throws<ArgumentException>(() =>
            ArtifactRelayValidator.NormalizeRelay(Guid.NewGuid().ToString(), Request(marker)));
    }

    [Fact]
    public void ZeroExperienceExplicitlyMeansNoAssociation()
    {
        var proof = new ArtifactBundleScriptProofDto("LSL", Masks, true, new string('b', 64),
            Guid.Empty.ToString());
        var marker = new ArtifactBundleMarkerDto("managed-runtime", Guid.NewGuid().ToString(),
            "LSLText", proof);

        var spec = ArtifactRelayValidator.NormalizeRelay(Guid.NewGuid().ToString(), Request(marker));

        Assert.Equal(Guid.Empty, Assert.Single(spec.BundleMarkers).ScriptProof!.ExperienceId);
    }

    private static ArtifactRelayRequestDto Request(ArtifactBundleMarkerDto marker) => new(
        "Briarmont",
        new Vector3Dto(128, 128, 25),
        "Titler Scanner",
        Guid.NewGuid().ToString(),
        Guid.NewGuid().ToString(),
        "titler-scanner",
        "munibase-artifact:11111111111141118111111111111111",
        [marker],
        Masks);
}

using Munibot;

namespace Munibot.Tests;

public sealed class TeleportRequestValidatorTests
{
    [Fact]
    public void NormalizeRegionName_TrimsRegion()
    {
        var region = TeleportRequestValidator.NormalizeRegionName(" Example Region ");

        Assert.Equal("Example Region", region);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeRegionName_RejectsMissingRegion(string? regionName)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            TeleportRequestValidator.NormalizeRegionName(regionName));

        Assert.Contains("region name is required", ex.Message);
    }

    [Fact]
    public void NormalizePosition_DefaultsToEstateAnchorSafePosition()
    {
        var position = TeleportRequestValidator.NormalizePosition(null);

        Assert.Equal(128, position.X);
        Assert.Equal(128, position.Y);
        Assert.Equal(25, position.Z);
    }

    [Fact]
    public void NormalizePosition_AcceptsValidRegionCoordinates()
    {
        var position = TeleportRequestValidator.NormalizePosition(new Vector3Dto(12.5f, 30, 1000));

        Assert.Equal(12.5f, position.X);
        Assert.Equal(30, position.Y);
        Assert.Equal(1000, position.Z);
    }

    [Theory]
    [InlineData(-1, 128, 25)]
    [InlineData(256, 128, 25)]
    [InlineData(128, -1, 25)]
    [InlineData(128, 256, 25)]
    [InlineData(128, 128, -1)]
    [InlineData(128, 128, 4097)]
    public void NormalizePosition_RejectsOutOfBoundsCoordinates(float x, float y, float z)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            TeleportRequestValidator.NormalizePosition(new Vector3Dto(x, y, z)));

        Assert.Contains("within region bounds", ex.Message);
    }

    [Fact]
    public void NormalizePosition_RejectsNonFiniteCoordinates()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            TeleportRequestValidator.NormalizePosition(new Vector3Dto(float.NaN, 128, 25)));

        Assert.Contains("finite numbers", ex.Message);
    }
}

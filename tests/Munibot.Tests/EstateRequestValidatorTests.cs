using Munibot;

namespace Munibot.Tests;

public sealed class EstateRequestValidatorTests
{
    [Theory]
    [InlineData("allow", EstateListEntryType.Allow)]
    [InlineData("access", EstateListEntryType.Allow)]
    [InlineData("user", EstateListEntryType.Allow)]
    [InlineData("ban", EstateListEntryType.Ban)]
    [InlineData("banned", EstateListEntryType.Ban)]
    public void NormalizeEntryType_AcceptsKnownAliases(string value, EstateListEntryType expected)
    {
        var result = EstateRequestValidator.NormalizeEntryType(value);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("manager")]
    public void NormalizeEntryType_RejectsInvalidValues(string? value)
    {
        Assert.Throws<ArgumentException>(() => EstateRequestValidator.NormalizeEntryType(value));
    }

    [Fact]
    public void NormalizeAnchorRegion_TrimsRegionName()
    {
        var result = EstateRequestValidator.NormalizeAnchorRegion(" Example Region ");

        Assert.Equal("Example Region", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NormalizeAnchorRegion_RequiresValue(string? value)
    {
        Assert.Throws<ArgumentException>(() => EstateRequestValidator.NormalizeAnchorRegion(value));
    }

    [Fact]
    public void NormalizeAvatarId_RejectsZeroUuid()
    {
        Assert.Throws<ArgumentException>(() =>
            EstateRequestValidator.NormalizeAvatarId("00000000-0000-0000-0000-000000000000"));
    }
}

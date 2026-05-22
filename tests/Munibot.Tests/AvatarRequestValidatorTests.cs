using Munibot;

namespace Munibot.Tests;

public sealed class AvatarRequestValidatorTests
{
    [Fact]
    public void NormalizeNames_TrimsAndDeduplicatesNames()
    {
        var names = AvatarRequestValidator.NormalizeNames(
            [" Example Resident ", "example resident", "Other Resident"]);

        Assert.Equal(["Example Resident", "Other Resident"], names);
    }

    [Fact]
    public void NormalizeNames_RejectsMissingNames()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            AvatarRequestValidator.NormalizeNames([" ", ""]));

        Assert.Contains("At least one avatar name", ex.Message);
    }

    [Fact]
    public void NormalizeAvatarIds_ParsesAndDeduplicatesUuids()
    {
        var ids = AvatarRequestValidator.NormalizeAvatarIds(
            [
                "11111111-1111-4111-8111-111111111111",
                "11111111-1111-4111-8111-111111111111"
            ]);

        var id = Assert.Single(ids);
        Assert.Equal("11111111-1111-4111-8111-111111111111", id.ToString());
    }

    [Fact]
    public void NormalizeAvatarIds_RejectsInvalidUuid()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            AvatarRequestValidator.NormalizeAvatarIds(["not-a-uuid"]));

        Assert.Contains("Invalid Second Life avatar UUID", ex.Message);
    }

    [Fact]
    public void NormalizeSearchText_TrimsQuery()
    {
        var query = AvatarRequestValidator.NormalizeSearchText(" Example ");

        Assert.Equal("Example", query);
    }

    [Fact]
    public void NormalizeSearchText_RejectsMissingQuery()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            AvatarRequestValidator.NormalizeSearchText(" "));

        Assert.Contains("Search text is required", ex.Message);
    }
}

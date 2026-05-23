using Munibot;

namespace Munibot.Tests;

public sealed class ExperienceRequestValidatorTests
{
    private const string ExperienceId = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public void NormalizeExperienceId_AcceptsValidUuid()
    {
        var parsed = ExperienceRequestValidator.NormalizeExperienceId($" {ExperienceId} ");

        Assert.Equal(ExperienceId, parsed.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-uuid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void NormalizeExperienceId_RejectsInvalidUuid(string experienceId)
    {
        Assert.Throws<ArgumentException>(() =>
            ExperienceRequestValidator.NormalizeExperienceId(experienceId));
    }
}

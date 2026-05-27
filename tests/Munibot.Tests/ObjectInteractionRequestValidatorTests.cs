using Munibot;

namespace Munibot.Tests;

public sealed class ObjectInteractionRequestValidatorTests
{
    [Theory]
    [InlineData(null, 5f)]
    [InlineData(0.5f, 0.5f)]
    [InlineData(5f, 5f)]
    [InlineData(96f, 96f)]
    public void NormalizeScanRadius_AcceptsValidRadius(float? radius, float expected)
    {
        var normalized = ObjectInteractionRequestValidator.NormalizeScanRadius(radius);

        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(97f)]
    public void NormalizeScanRadius_RejectsOutOfRangeRadius(float radius)
    {
        Assert.Throws<ArgumentException>(() => ObjectInteractionRequestValidator.NormalizeScanRadius(radius));
    }

    [Fact]
    public void NormalizeObjectId_RequiresNonZeroUuid()
    {
        Assert.Throws<ArgumentException>(() => ObjectInteractionRequestValidator.NormalizeObjectId(null));
        Assert.Throws<ArgumentException>(() => ObjectInteractionRequestValidator.NormalizeObjectId("00000000-0000-0000-0000-000000000000"));
    }

    [Theory]
    [InlineData("sit", "sit")]
    [InlineData(" touch ", "touch")]
    [InlineData("SIT", "sit")]
    public void NormalizeAction_AcceptsSupportedActions(string action, string expected)
    {
        var normalized = ObjectInteractionRequestValidator.NormalizeAction(action);

        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("pay")]
    [InlineData("grab")]
    public void NormalizeAction_RejectsUnsupportedActions(string action)
    {
        Assert.Throws<ArgumentException>(() => ObjectInteractionRequestValidator.NormalizeAction(action));
    }

    [Fact]
    public void NormalizeSitOffset_DefaultsToZero()
    {
        var offset = ObjectInteractionRequestValidator.NormalizeSitOffset(null);

        Assert.Equal(0, offset.X);
        Assert.Equal(0, offset.Y);
        Assert.Equal(0, offset.Z);
    }
}

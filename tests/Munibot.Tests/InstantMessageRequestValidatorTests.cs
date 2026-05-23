using Munibot;

namespace Munibot.Tests;

public sealed class InstantMessageRequestValidatorTests
{
    private const string AvatarId = "11111111-1111-4111-8111-111111111111";

    [Fact]
    public void NormalizeAvatarId_ParsesAvatarUuid()
    {
        var parsed = InstantMessageRequestValidator.NormalizeAvatarId($" {AvatarId} ");

        Assert.Equal(AvatarId, parsed.ToString());
    }

    [Fact]
    public void NormalizeAvatarId_RejectsInvalidAvatarUuid()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            InstantMessageRequestValidator.NormalizeAvatarId("not-a-uuid"));

        Assert.Contains("avatar UUID", ex.Message);
    }

    [Fact]
    public void NormalizeMessage_TrimsMessage()
    {
        var message = InstantMessageRequestValidator.NormalizeMessage(" hello ");

        Assert.Equal("hello", message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeMessage_RejectsMissingMessage(string? message)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            InstantMessageRequestValidator.NormalizeMessage(message));

        Assert.Contains("message text is required", ex.Message);
    }

    [Fact]
    public void NormalizeMessage_RejectsOverlongMessage()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            InstantMessageRequestValidator.NormalizeMessage(new string('x', InstantMessageRequestValidator.MaxMessageLength + 1)));

        Assert.Contains("characters or fewer", ex.Message);
    }
}

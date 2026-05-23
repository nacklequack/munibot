using OpenMetaverse;

namespace Munibot.Tests;

public sealed class LocalChatRequestValidatorTests
{
    [Fact]
    public void NormalizeMessage_TrimsMessage()
    {
        var message = LocalChatRequestValidator.NormalizeMessage(" hello ");

        Assert.Equal("hello", message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeMessage_RejectsMissingMessage(string? message)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            LocalChatRequestValidator.NormalizeMessage(message));

        Assert.Contains("message text is required", ex.Message);
    }

    [Fact]
    public void NormalizeMessage_RejectsOverlongMessage()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            LocalChatRequestValidator.NormalizeMessage(new string('x', LocalChatRequestValidator.MaxMessageLength + 1)));

        Assert.Contains("characters or fewer", ex.Message);
    }

    [Fact]
    public void NormalizeChannel_DefaultsToZero()
    {
        Assert.Equal(0, LocalChatRequestValidator.NormalizeChannel(null));
    }

    [Theory]
    [InlineData("normal", ChatType.Normal)]
    [InlineData("say", ChatType.Normal)]
    [InlineData("whisper", ChatType.Whisper)]
    [InlineData("shout", ChatType.Shout)]
    [InlineData(null, ChatType.Normal)]
    public void NormalizeChatType_ParsesSupportedTypes(string? chatType, ChatType expected)
    {
        Assert.Equal(expected, LocalChatRequestValidator.NormalizeChatType(chatType));
    }

    [Fact]
    public void NormalizeChatType_RejectsUnsupportedType()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            LocalChatRequestValidator.NormalizeChatType("yell"));

        Assert.Contains("normal, whisper, shout", ex.Message);
    }
}

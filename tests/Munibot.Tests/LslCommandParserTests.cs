using Munibot;

namespace Munibot.Tests;

public sealed class LslCommandParserTests
{
    private const string ObjectId = "11111111-1111-4111-8111-111111111111";

    [Fact]
    public void TryParseSitCommand_AcceptsValidCommand()
    {
        var parsed = LslCommandParser.TryParseSitCommand(
            $"munibot sit hush {ObjectId}",
            "hush",
            out var command,
            out var failureReason);

        Assert.True(parsed);
        Assert.Null(failureReason);
        Assert.Equal(ObjectId, command?.ObjectId.ToString());
        Assert.Null(command?.SitOffset);
    }

    [Fact]
    public void TryParseSitCommand_AcceptsOptionalOffset()
    {
        var parsed = LslCommandParser.TryParseSitCommand(
            $"munibot sit hush {ObjectId} offset=<0.1,0,-0.25>",
            "hush",
            out var command,
            out var failureReason);

        Assert.True(parsed);
        Assert.Null(failureReason);
        Assert.Equal(0.1f, command?.SitOffset?.X);
        Assert.Equal(0, command?.SitOffset?.Y);
        Assert.Equal(-0.25f, command?.SitOffset?.Z);
    }

    [Fact]
    public void TryParseSitCommand_IgnoresNonMunibotMessages()
    {
        var parsed = LslCommandParser.TryParseSitCommand(
            "hello there",
            "hush",
            out var command,
            out var failureReason);

        Assert.False(parsed);
        Assert.Null(command);
        Assert.Null(failureReason);
    }

    [Theory]
    [InlineData("munibot sit nope 11111111-1111-4111-8111-111111111111", "Shared secret did not match.")]
    [InlineData("munibot sit hush not-a-uuid", "Object UUID was invalid.")]
    [InlineData("munibot dance hush 11111111-1111-4111-8111-111111111111", "Expected command format")]
    [InlineData("munibot sit hush 11111111-1111-4111-8111-111111111111 offset=oops", "Sit offset was invalid.")]
    public void TryParseSitCommand_RejectsInvalidCommand(string message, string expectedReason)
    {
        var parsed = LslCommandParser.TryParseSitCommand(
            message,
            "hush",
            out var command,
            out var failureReason);

        Assert.False(parsed);
        Assert.Null(command);
        Assert.Contains(expectedReason, failureReason);
    }
}

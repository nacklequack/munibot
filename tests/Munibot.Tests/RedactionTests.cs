using Munibot;

namespace Munibot.Tests;

public sealed class RedactionTests
{
    [Fact]
    public void RedactText_RemovesBearerTokensAndPasswords()
    {
        var redacted = Redaction.RedactText("Authorization: Bearer abcdefghijklmnop password=supersecret munibot sit chair-secret 11111111-1111-4111-8111-111111111111");

        Assert.Contains("Bearer [redacted]", redacted);
        Assert.Contains("password=[redacted]", redacted);
        Assert.DoesNotContain("abcdefghijklmnop", redacted);
        Assert.DoesNotContain("supersecret", redacted);
        Assert.Contains("munibot sit [redacted] 11111111-1111-4111-8111-111111111111", redacted);
        Assert.DoesNotContain("chair-secret", redacted);
    }

    [Fact]
    public void RedactJsonOrText_RedactsSensitiveJsonProperties()
    {
        var body = """
            {
              "avatarUuid": "11111111-1111-4111-8111-111111111111",
              "token": "secret-token",
              "paymentDescription": "rent payment",
              "nested": {
                "password": "pass"
              }
            }
            """;

        var redacted = Redaction.RedactJsonOrText(body, 4096);

        Assert.Contains("11111111-1111-4111-8111-111111111111", redacted);
        Assert.DoesNotContain("secret-token", redacted);
        Assert.DoesNotContain("rent payment", redacted);
        Assert.DoesNotContain("\"pass\"", redacted);
    }

    [Fact]
    public void RedactText_TruncatesLongMessages()
    {
        var redacted = Redaction.RedactText(new string('x', 100), maxLength: 12);

        Assert.Equal("xxxxxxxxxxxx...[truncated]", redacted);
    }
}

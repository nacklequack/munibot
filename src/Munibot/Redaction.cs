using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Munibot;

public static partial class Redaction
{
    private static readonly string[] SensitiveNameParts =
    [
        "token",
        "password",
        "secret",
        "authorization",
        "cookie",
        "mfa",
        "description",
        "payment",
        "payload",
        "base64",
        "texture",
        "data"
    ];

    public static string RedactText(string? value, int maxLength = 512)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var redacted = BearerTokenRegex().Replace(value, "$1[redacted]");
        redacted = PasswordLikeRegex().Replace(redacted, "$1[redacted]");
        redacted = LongBase64Regex().Replace(redacted, "[redacted-large-token]");

        return redacted.Length <= maxLength
            ? redacted
            : string.Concat(redacted.AsSpan(0, maxLength), "...[truncated]");
    }

    public static string RedactJsonOrText(string? body, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            var node = JsonNode.Parse(body);
            if (node is not null)
            {
                RedactJsonNode(node, null, maxLength);
                return RedactText(node.ToJsonString(new JsonSerializerOptions { WriteIndented = false }), maxLength);
            }
        }
        catch (JsonException)
        {
            // Fall through to plain text redaction.
        }

        return RedactText(body, maxLength);
    }

    private static void RedactJsonNode(JsonNode node, string? propertyName, int maxLength)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(kvp => kvp.Key).ToList())
            {
                var child = obj[key];
                if (child is null)
                {
                    continue;
                }

                if (IsSensitivePropertyName(key))
                {
                    obj[key] = "[redacted]";
                    continue;
                }

                RedactJsonNode(child, key, maxLength);
            }
            return;
        }

        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not null)
                {
                    RedactJsonNode(item, propertyName, maxLength);
                }
            }
            return;
        }

        if (node is JsonValue valueNode &&
            valueNode.TryGetValue<string>(out var stringValue) &&
            (IsSensitivePropertyName(propertyName) || stringValue.Length > maxLength || LooksLikeSecret(stringValue)))
        {
            valueNode.ReplaceWith("[redacted]");
        }
    }

    private static bool IsSensitivePropertyName(string? propertyName)
        => !string.IsNullOrWhiteSpace(propertyName) &&
           SensitiveNameParts.Any(part => propertyName.Contains(part, StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikeSecret(string value)
        => value.Length > 80 && LongBase64Regex().IsMatch(value);

    [GeneratedRegex(@"(?i)(bearer\s+)([A-Za-z0-9._~+/=-]{8,})")]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"(?i)(password\s*[=:]\s*)([^\s,;]+)")]
    private static partial Regex PasswordLikeRegex();

    [GeneratedRegex(@"[A-Za-z0-9+/]{120,}={0,2}")]
    private static partial Regex LongBase64Regex();
}

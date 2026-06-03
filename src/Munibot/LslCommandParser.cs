using System.Globalization;
using OpenMetaverse;

namespace Munibot;

public sealed record LslSitCommand(
    UUID ObjectId,
    Vector3Dto? SitOffset);

public static class LslCommandParser
{
    public static bool TryParseSitCommand(
        string? message,
        string? sharedSecret,
        out LslSitCommand? command,
        out string? failureReason)
    {
        command = null;
        failureReason = null;

        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var normalized = message.Trim();
        if (!normalized.StartsWith("munibot", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(sharedSecret))
        {
            failureReason = "LSL commands are not configured.";
            return false;
        }

        var tokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokens.Length < 4 ||
            !string.Equals(tokens[0], "munibot", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(tokens[1], "sit", StringComparison.OrdinalIgnoreCase))
        {
            failureReason = "Expected command format 'munibot sit <secret> <object-uuid>'.";
            return false;
        }

        if (!string.Equals(tokens[2], sharedSecret, StringComparison.Ordinal))
        {
            failureReason = "Shared secret did not match.";
            return false;
        }

        if (!UUID.TryParse(tokens[3], out var objectId) || objectId == UUID.Zero)
        {
            failureReason = "Object UUID was invalid.";
            return false;
        }

        Vector3Dto? sitOffset = null;
        foreach (var token in tokens.Skip(4))
        {
            if (!token.StartsWith("offset=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryParseVector(token["offset=".Length..], out sitOffset))
            {
                failureReason = "Sit offset was invalid.";
                return false;
            }
        }

        command = new LslSitCommand(objectId, sitOffset);
        return true;
    }

    private static bool TryParseVector(string rawValue, out Vector3Dto? vector)
    {
        vector = null;

        var normalized = rawValue.Trim().Trim('<', '>');
        var parts = normalized.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
        {
            return false;
        }

        if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
            !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
        {
            return false;
        }

        vector = new Vector3Dto(x, y, z);
        return true;
    }
}

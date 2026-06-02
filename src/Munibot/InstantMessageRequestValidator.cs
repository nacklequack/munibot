using OpenMetaverse;

namespace Munibot;

public static class InstantMessageRequestValidator
{
    public const int MaxMessageLength = 1023;

    public static UUID NormalizeAvatarId(string? avatarId)
    {
        var trimmed = avatarId?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) ||
            !UUID.TryParse(trimmed, out var parsed) ||
            parsed == UUID.Zero)
        {
            throw new ArgumentException("A valid Second Life avatar UUID is required.");
        }

        return parsed;
    }

    public static string NormalizeMessage(string? message)
    {
        var trimmed = message?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new ArgumentException("Instant message text is required.");
        }

        if (trimmed.Length > MaxMessageLength)
        {
            throw new ArgumentException($"Instant message text must be {MaxMessageLength} characters or fewer.");
        }

        return trimmed;
    }
}

using OpenMetaverse;

namespace Munibot;

public static class AvatarRequestValidator
{
    public static IReadOnlyList<string> NormalizeNames(IEnumerable<string>? names)
    {
        var normalized = (names ?? Array.Empty<string>())
            .Select(name => name?.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalized.Count == 0)
        {
            throw new ArgumentException("At least one avatar name is required.");
        }

        foreach (var name in normalized)
        {
            if (name.Length > 128)
            {
                throw new ArgumentException("Avatar names must be 128 characters or fewer.");
            }
        }

        return normalized;
    }

    public static IReadOnlyList<UUID> NormalizeAvatarIds(IEnumerable<string>? avatarIds)
    {
        var normalized = new List<UUID>();
        foreach (var avatarId in avatarIds ?? Array.Empty<string>())
        {
            var trimmed = avatarId?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) ||
                !UUID.TryParse(trimmed, out var parsed) ||
                parsed == UUID.Zero)
            {
                throw new ArgumentException($"Invalid Second Life avatar UUID '{avatarId}'.");
            }

            if (!normalized.Contains(parsed))
            {
                normalized.Add(parsed);
            }
        }

        if (normalized.Count == 0)
        {
            throw new ArgumentException("At least one avatar UUID is required.");
        }

        return normalized;
    }

    public static string NormalizeSearchText(string? searchText)
    {
        var normalized = searchText?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Search text is required.");
        }

        if (normalized.Length > 128)
        {
            throw new ArgumentException("Search text must be 128 characters or fewer.");
        }

        return normalized;
    }
}

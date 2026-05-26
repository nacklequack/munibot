using OpenMetaverse;

namespace Munibot;

public enum EstateListEntryType
{
    Allow,
    Ban
}

public enum EstateListAction
{
    Add,
    Remove
}

public static class EstateRequestValidator
{
    public static EstateListEntryType NormalizeEntryType(string? entryType)
    {
        var trimmed = entryType?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new ArgumentException("Estate list type is required.");
        }

        return trimmed.ToLowerInvariant() switch
        {
            "allow" or "allowed" or "access" or "user" or "users" => EstateListEntryType.Allow,
            "ban" or "bans" or "banned" => EstateListEntryType.Ban,
            _ => throw new ArgumentException("Estate list type must be 'allow' or 'ban'.")
        };
    }

    public static string NormalizeAnchorRegion(string? anchorRegion)
    {
        var trimmed = anchorRegion?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new ArgumentException("Estate anchor region is required.");
        }

        if (trimmed.Length > 64)
        {
            throw new ArgumentException("Estate anchor region must be 64 characters or fewer.");
        }

        return trimmed;
    }

    public static UUID NormalizeAvatarId(string? avatarId)
    {
        var trimmed = avatarId?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) ||
            !UUID.TryParse(trimmed, out var parsed) ||
            parsed == UUID.Zero)
        {
            throw new ArgumentException("A valid non-zero Second Life avatar UUID is required.");
        }

        return parsed;
    }

    public static string ToWireValue(EstateListEntryType entryType)
        => entryType == EstateListEntryType.Allow ? "allow" : "ban";

    public static string ToWireValue(EstateListAction action)
        => action == EstateListAction.Add ? "add" : "remove";
}

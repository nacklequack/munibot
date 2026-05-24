using OpenMetaverse;

namespace Munibot;

public static class InventoryRequestValidator
{
    public const int MaxItemNameLength = 63;
    public const int MaxItemPathLength = 512;

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

    public static UUID? NormalizeItemId(string? itemId)
    {
        var trimmed = itemId?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        if (!UUID.TryParse(trimmed, out var parsed) || parsed == UUID.Zero)
        {
            throw new ArgumentException("A valid Second Life inventory item UUID is required.");
        }

        return parsed;
    }

    public static string? NormalizeItemPath(string? itemPath)
    {
        var trimmed = itemPath?.Trim().Trim('/');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        if (trimmed.Length > MaxItemPathLength)
        {
            throw new ArgumentException($"Inventory item path must be {MaxItemPathLength} characters or fewer.");
        }

        return trimmed;
    }

    public static string? NormalizeItemName(string? itemName)
    {
        var trimmed = itemName?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        if (trimmed.Length > MaxItemNameLength)
        {
            throw new ArgumentException($"Inventory item name must be {MaxItemNameLength} characters or fewer.");
        }

        return trimmed;
    }

    public static AssetType? NormalizeAssetType(string? assetType)
    {
        var trimmed = assetType?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        if (!Enum.TryParse<AssetType>(trimmed, ignoreCase: true, out var parsed))
        {
            throw new ArgumentException($"Unsupported Second Life inventory asset type '{trimmed}'.");
        }

        return parsed;
    }

    public static bool NormalizeDoEffect(bool? doEffect)
        => doEffect ?? true;
}

using OpenMetaverse;

namespace Munibot;

public static class ObjectInteractionRequestValidator
{
    public const float DefaultScanRadiusMeters = 5f;
    public const float MaxScanRadiusMeters = 96f;

    private static readonly HashSet<string> SupportedActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "sit",
        "touch"
    };

    public static float NormalizeScanRadius(float? radius)
    {
        var normalized = radius ?? DefaultScanRadiusMeters;
        if (!IsFinite(normalized))
        {
            throw new ArgumentException("Object scan radius must be a finite number.");
        }

        if (normalized <= 0 || normalized > MaxScanRadiusMeters)
        {
            throw new ArgumentException($"Object scan radius must be greater than 0 and no more than {MaxScanRadiusMeters} meters.");
        }

        return normalized;
    }

    public static string? NormalizeNameFilter(string? name)
    {
        var normalized = name?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (normalized.Length > 128)
        {
            throw new ArgumentException("Object name filter cannot exceed 128 characters.");
        }

        return normalized;
    }

    public static UUID NormalizeObjectId(string? objectId)
    {
        var normalized = objectId?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) ||
            !UUID.TryParse(normalized, out var parsed) ||
            parsed == UUID.Zero)
        {
            throw new ArgumentException("A valid object UUID is required.");
        }

        return parsed;
    }

    public static string NormalizeAction(string? action)
    {
        var normalized = action?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized) || !SupportedActions.Contains(normalized))
        {
            throw new ArgumentException("Object interaction action must be 'sit' or 'touch'.");
        }

        return normalized;
    }

    public static Vector3 NormalizeSitOffset(Vector3Dto? offset)
    {
        if (offset is null)
        {
            return Vector3.Zero;
        }

        if (!IsFinite(offset.X) || !IsFinite(offset.Y) || !IsFinite(offset.Z))
        {
            throw new ArgumentException("Sit offset coordinates must be finite numbers.");
        }

        if (MathF.Abs(offset.X) > 256 || MathF.Abs(offset.Y) > 256 || MathF.Abs(offset.Z) > 256)
        {
            throw new ArgumentException("Sit offset coordinates must be between -256 and 256 meters.");
        }

        return new Vector3(offset.X, offset.Y, offset.Z);
    }

    private static bool IsFinite(float value)
        => !float.IsNaN(value) && !float.IsInfinity(value);
}

using OpenMetaverse;

namespace Munibot;

public static class TeleportRequestValidator
{
    public static Vector3 NormalizePosition(Vector3Dto? position)
    {
        var normalized = position ?? new Vector3Dto(128, 128, 25);
        if (!IsFinite(normalized.X) || !IsFinite(normalized.Y) || !IsFinite(normalized.Z))
        {
            throw new ArgumentException("Teleport position coordinates must be finite numbers.");
        }

        if (normalized.X < 0 || normalized.X > 255 ||
            normalized.Y < 0 || normalized.Y > 255 ||
            normalized.Z < 0 || normalized.Z > 4096)
        {
            throw new ArgumentException("Teleport position must be within region bounds: x/y 0-255, z 0-4096.");
        }

        return new Vector3(normalized.X, normalized.Y, normalized.Z);
    }

    public static string NormalizeRegionName(string? regionName)
    {
        var normalized = regionName?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("A Second Life region name is required.");
        }

        if (normalized.Length > 128)
        {
            throw new ArgumentException("Second Life region name is too long.");
        }

        return normalized;
    }

    private static bool IsFinite(float value)
        => !float.IsNaN(value) && !float.IsInfinity(value);
}

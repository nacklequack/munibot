using OpenMetaverse;

namespace Munibot;

public static class GroupRequestValidator
{
    public static UUID NormalizeGroupId(string? groupUuid)
    {
        var trimmed = groupUuid?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) ||
            !UUID.TryParse(trimmed, out var groupId) ||
            groupId == UUID.Zero)
        {
            throw new ArgumentException("A valid Second Life group UUID is required.");
        }

        return groupId;
    }

    public static UUID NormalizeAvatarId(string? avatarUuid)
    {
        var trimmed = avatarUuid?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) ||
            !UUID.TryParse(trimmed, out var avatarId) ||
            avatarId == UUID.Zero)
        {
            throw new ArgumentException("A valid Second Life avatar UUID is required.");
        }

        return avatarId;
    }

    public static IReadOnlyList<UUID> NormalizeRoleIds(IEnumerable<string>? roleUuids)
    {
        var roles = new List<UUID>();
        foreach (var roleUuid in roleUuids ?? Array.Empty<string>())
        {
            var trimmed = roleUuid?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || !UUID.TryParse(trimmed, out var roleId))
            {
                throw new ArgumentException($"Invalid Second Life group role UUID '{roleUuid}'.");
            }

            if (!roles.Contains(roleId))
            {
                roles.Add(roleId);
            }
        }

        if (roles.Count == 0)
        {
            roles.Add(UUID.Zero);
        }

        return roles;
    }
}

using Munibot;
using OpenMetaverse;

namespace Munibot.Tests;

public sealed class GroupRequestValidatorTests
{
    private const string GroupId = "22222222-2222-4222-8222-222222222222";
    private const string AvatarId = "11111111-1111-4111-8111-111111111111";

    [Fact]
    public void NormalizeGroupId_ParsesGroupUuid()
    {
        var parsed = GroupRequestValidator.NormalizeGroupId($" {GroupId} ");

        Assert.Equal(GroupId, parsed.ToString());
    }

    [Fact]
    public void NormalizeGroupId_RejectsInvalidGroupUuid()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            GroupRequestValidator.NormalizeGroupId("not-a-uuid"));

        Assert.Contains("group UUID", ex.Message);
    }

    [Fact]
    public void NormalizeAvatarId_ParsesAvatarUuid()
    {
        var parsed = GroupRequestValidator.NormalizeAvatarId($" {AvatarId} ");

        Assert.Equal(AvatarId, parsed.ToString());
    }

    [Fact]
    public void NormalizeAvatarId_RejectsInvalidAvatarUuid()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            GroupRequestValidator.NormalizeAvatarId("not-a-uuid"));

        Assert.Contains("avatar UUID", ex.Message);
    }

    [Fact]
    public void NormalizeRoleIds_DefaultsToEveryoneRole()
    {
        var roleIds = GroupRequestValidator.NormalizeRoleIds([]);

        var roleId = Assert.Single(roleIds);
        Assert.Equal(UUID.Zero, roleId);
    }

    [Fact]
    public void NormalizeRoleIds_DeduplicatesRoles()
    {
        var roleIds = GroupRequestValidator.NormalizeRoleIds([GroupId, GroupId.ToUpperInvariant()]);

        var roleId = Assert.Single(roleIds);
        Assert.Equal(GroupId, roleId.ToString());
    }

    [Fact]
    public void NormalizeRoleIds_RejectsInvalidRole()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            GroupRequestValidator.NormalizeRoleIds(["bad-role"]));

        Assert.Contains("group role UUID", ex.Message);
    }
}

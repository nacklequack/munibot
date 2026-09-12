using OpenMetaverse;
using OpenMetaverse.StructuredData;
using System.Xml.Linq;

namespace Munibot.Tests;

public sealed class TaskInventorySharingTests
{
    private static readonly UUID Bot = UUID.Random();
    private static readonly UUID Group = UUID.Random();

    [Fact]
    public void OtherOwnerTargetRequiresMatchingActiveGroupAndObjectSharing()
    {
        var target = Target();
        Assert.Equal(Group, TaskInventorySharing.SelectGroup(Bot, Group, target));
        Assert.Throws<TaskInventoryException>(() => TaskInventorySharing.SelectGroup(Bot, UUID.Random(), target));
        target.Permissions.GroupMask = PermissionMask.None;
        Assert.Throws<TaskInventoryException>(() => TaskInventorySharing.SelectGroup(Bot, Group, target));
        target.GroupID = UUID.Zero;
        Assert.Throws<TaskInventoryException>(() => TaskInventorySharing.SelectGroup(Bot, UUID.Zero, target));
    }

    [Fact]
    public void BotOwnedUnsharedTargetDoesNotGrantGroupAccess()
    {
        var target = Target();
        target.OwnerID = Bot;
        target.Permissions.GroupMask = PermissionMask.None;
        Assert.Equal(UUID.Zero, TaskInventorySharing.SelectGroup(Bot, Group, target));
    }

    [Fact]
    public async Task UnsharedBotOwnedTargetDoesNotPatchTemporaryPermissions()
    {
        var item = Temporary();
        var sharing = new TaskInventorySharing((_, _, _) => throw new Exception("Unexpected permission update"),
            (_, _, _) => throw new Exception("Unexpected fetch"));
        Assert.Same(item, await sharing.PrepareAsync(item, Bot, UUID.Zero, default));
    }

    [Theory]
    [InlineData(AssetType.LSLText)]
    [InlineData(AssetType.Notecard)]
    public async Task SharingWaitsForAcknowledgementThenFetchesActualItemBeforeCopy(AssetType type)
    {
        var item = Temporary(type);
        var acknowledged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetched = 0;
        var sharing = new TaskInventorySharing((id, patch, _) =>
        {
            Assert.Equal(item.UUID, id);
            Assert.Single(patch);
            var permissions = Assert.IsType<OSDMap>(patch["permissions"]);
            Assert.Equal(2, permissions.Count);
            Assert.Equal(Group, permissions["group_id"].AsUUID());
            Assert.Equal((uint)TaskInventorySharing.SharedSourceMask, permissions["group_mask"].AsUInteger());
            Assert.False(permissions.ContainsKey("everyone_mask"));
            Assert.False(permissions.ContainsKey("next_owner_mask"));
            Assert.False(permissions.ContainsKey("owner_id"));
            return acknowledged.Task;
        }, (id, owner, _) =>
        {
            fetched++;
            Assert.Equal(item.UUID, id);
            Assert.Equal(Bot, owner);
            return Task.FromResult<InventoryItem?>(Prepared(item));
        });
        var operation = sharing.PrepareAsync(item, Bot, Group, default);
        Assert.False(operation.IsCompleted);
        Assert.Equal(0, fetched);
        acknowledged.SetResult(true);
        var result = await operation;
        Assert.Equal(Group, result.GroupID);
        Assert.Equal(TaskInventorySharing.SharedSourceMask, result.Permissions.GroupMask);
        Assert.Equal(PermissionMask.None, item.Permissions.GroupMask);
        Assert.Equal(1, fetched);
    }

    [Fact]
    public async Task PermissionMaskIsAnLlsdIntegerOnTheWire()
    {
        var item = Temporary();
        var sharing = new TaskInventorySharing((_, patch, _) =>
        {
            var xml = XDocument.Parse(OSDParser.SerializeLLSDXmlString(patch));
            var maskKey = Assert.Single(xml.Descendants("key").Where(key => key.Value == "group_mask"));
            var wireValue = maskKey.ElementsAfterSelf().First();
            Assert.Equal("integer", wireValue.Name.LocalName);
            Assert.Equal("573440", wireValue.Value);

            var decoded = Assert.IsType<OSDMap>(OSDParser.Deserialize(xml.ToString()));
            var permissions = Assert.IsType<OSDMap>(decoded["permissions"]);
            Assert.Equal(OSDType.Integer, permissions["group_mask"].Type);
            Assert.Equal(TaskInventorySharing.SharedSourceMask, Permissions.FromOSD(permissions).GroupMask);
            Assert.Equal(OSDType.UUID, permissions["group_id"].Type);
            return Task.FromResult(true);
        }, (_, _, _) => Task.FromResult<InventoryItem?>(Prepared(item)));
        await sharing.PrepareAsync(item, Bot, Group, default);
    }

    [Fact]
    public async Task ServerRejectionDoesNotReturnCopyableItem()
    {
        var sharing = new TaskInventorySharing((_, _, _) => Task.FromResult(false),
            (_, _, _) => throw new Exception("A rejected update must not proceed"));
        var error = await Assert.ThrowsAsync<TaskInventoryException>(() => sharing.PrepareAsync(Temporary(), Bot, Group, default));
        Assert.Equal("source_sharing_denied", error.Code);
        Assert.False(error.OutcomeUnknown);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("id")]
    [InlineData("owner")]
    [InlineData("group-owned")]
    [InlineData("group")]
    [InlineData("group-mask")]
    [InlineData("base")]
    [InlineData("owner-mask")]
    [InlineData("everyone")]
    [InlineData("next-owner")]
    [InlineData("asset")]
    [InlineData("type")]
    [InlineData("name")]
    public async Task ReadbackMustPreserveIdentitySourceAndUnrelatedPermissions(string mismatch)
    {
        var item = Temporary();
        InventoryItem? returned = Prepared(item);
        switch (mismatch)
        {
            case "missing": returned = null; break;
            case "id": returned.UUID = UUID.Random(); break;
            case "owner": returned.OwnerID = UUID.Random(); break;
            case "group-owned": returned.GroupOwned = true; break;
            case "group": returned.GroupID = UUID.Random(); break;
            case "group-mask": returned.Permissions.GroupMask = PermissionMask.None; break;
            case "base": returned.Permissions.BaseMask &= ~PermissionMask.Transfer; break;
            case "owner-mask": returned.Permissions.OwnerMask &= ~PermissionMask.Transfer; break;
            case "everyone": returned.Permissions.EveryoneMask |= PermissionMask.Copy; break;
            case "next-owner": returned.Permissions.NextOwnerMask &= ~PermissionMask.Copy; break;
            case "asset": returned.AssetUUID = UUID.Random(); break;
            case "type": returned.AssetType = AssetType.Texture; break;
            case "name": returned.Name = "Changed"; break;
        }
        var sharing = new TaskInventorySharing((_, _, _) => Task.FromResult(true), (_, _, _) => Task.FromResult(returned));
        var error = await Assert.ThrowsAsync<TaskInventoryException>(() => sharing.PrepareAsync(item, Bot, Group, default));
        Assert.Equal("source_sharing_unconfirmed", error.Code);
        Assert.False(error.OutcomeUnknown);
    }

    [Fact]
    public async Task MutatingTheSdkCachedItemCannotHideAnUnrelatedPermissionChange()
    {
        var item = Temporary();
        var sharing = new TaskInventorySharing((_, _, _) => Task.FromResult(true), (_, _, _) =>
        {
            item.GroupID = Group;
            item.Permissions.GroupMask = TaskInventorySharing.SharedSourceMask;
            item.Permissions.EveryoneMask = PermissionMask.Copy;
            return Task.FromResult<InventoryItem?>(item);
        });
        var error = await Assert.ThrowsAsync<TaskInventoryException>(() => sharing.PrepareAsync(item, Bot, Group, default));
        Assert.Equal("source_sharing_unconfirmed", error.Code);
    }

    [Fact]
    public async Task CancelledFetchCannotAuthorizeCopyFromCachedOrMissingItem()
    {
        using var cancellation = new CancellationTokenSource();
        var sharing = new TaskInventorySharing((_, _, _) => Task.FromResult(true), (_, _, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult<InventoryItem?>(null);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sharing.PrepareAsync(Temporary(), Bot, Group, cancellation.Token));
    }

    [Fact]
    public async Task CannotChangePermissionsOfAnotherOwnersTemporaryItem()
    {
        var item = Temporary();
        item.OwnerID = UUID.Random();
        var sharing = new TaskInventorySharing((_, _, _) => throw new Exception("Must not update"),
            (_, _, _) => throw new Exception("Must not fetch"));
        var error = await Assert.ThrowsAsync<TaskInventoryException>(() => sharing.PrepareAsync(item, Bot, Group, default));
        Assert.Equal("temporary_owner_mismatch", error.Code);
    }

    [Fact]
    public void CopyMustRetainSharingAfterOwnershipChanges()
    {
        var copied = Prepared(Temporary());
        copied.OwnerID = UUID.Random();
        TaskInventorySharing.VerifyCopy(copied, Group);
        copied.Permissions.GroupMask = PermissionMask.None;
        var error = Assert.Throws<TaskInventoryException>(() => TaskInventorySharing.VerifyCopy(copied, Group));
        Assert.True(error.OutcomeUnknown);
        Assert.False(error.Retryable);
        TaskInventorySharing.VerifyCopy(copied, UUID.Zero);
    }

    private static Primitive.ObjectProperties Target() => new()
    {
        ObjectID = UUID.Random(), OwnerID = UUID.Random(), GroupID = Group,
        Permissions = new() { GroupMask = PermissionMask.Modify | PermissionMask.Move }
    };

    private static InventoryItem Temporary(AssetType type = AssetType.LSLText) => new(UUID.Random())
    {
        OwnerID = Bot, GroupID = UUID.Zero, Name = "Example source", AssetUUID = UUID.Random(), AssetType = type,
        Permissions = new()
        {
            BaseMask = PermissionMask.All, OwnerMask = PermissionMask.All, NextOwnerMask = PermissionMask.All,
            GroupMask = PermissionMask.None, EveryoneMask = PermissionMask.None
        }
    };

    private static InventoryItem Prepared(InventoryItem before) => new(before.UUID)
    {
        OwnerID = before.OwnerID, GroupID = Group, Name = before.Name, AssetUUID = before.AssetUUID, AssetType = before.AssetType,
        Permissions = new()
        {
            BaseMask = before.Permissions.BaseMask, OwnerMask = before.Permissions.OwnerMask,
            NextOwnerMask = before.Permissions.NextOwnerMask, EveryoneMask = before.Permissions.EveryoneMask,
            GroupMask = TaskInventorySharing.SharedSourceMask
        }
    };
}

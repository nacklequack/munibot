using OpenMetaverse;
using OpenMetaverse.Assets;
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
        var sharing = Sharing((_, _, _) => throw new Exception("Unexpected permission update"),
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
        var sharing = Sharing((id, patch, _) =>
        {
            Assert.Equal(item.UUID, id);
            Assert.Single(patch);
            var permissions = Assert.IsType<OSDMap>(patch["permissions"]);
            Assert.Single(permissions);
            Assert.False(permissions.ContainsKey("group_id"));
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
        var sharing = Sharing((_, patch, _) =>
        {
            var xml = XDocument.Parse(OSDParser.SerializeLLSDXmlString(patch));
            var maskKey = Assert.Single(xml.Descendants("key"), key => key.Value == "group_mask");
            var wireValue = maskKey.ElementsAfterSelf().First();
            Assert.Equal("integer", wireValue.Name.LocalName);
            Assert.Equal("573440", wireValue.Value);

            var decoded = Assert.IsType<OSDMap>(OSDParser.Deserialize(xml.ToString()));
            var permissions = Assert.IsType<OSDMap>(decoded["permissions"]);
            Assert.Equal(OSDType.Integer, permissions["group_mask"].Type);
            Assert.Equal(TaskInventorySharing.SharedSourceMask, Permissions.FromOSD(permissions).GroupMask);
            Assert.False(permissions.ContainsKey("group_id"));
            return Task.FromResult(true);
        }, (_, _, _) => Task.FromResult<InventoryItem?>(Prepared(item)));
        await sharing.PrepareAsync(item, Bot, Group, default);
    }

    [Fact]
    public async Task ServerRejectionDoesNotReturnCopyableItem()
    {
        var sharing = Sharing((_, _, _) => Task.FromResult(false),
            (_, _, _) => throw new Exception("A rejected update must not proceed"));
        var error = await Assert.ThrowsAsync<TaskInventoryException>(() => sharing.PrepareAsync(Temporary(), Bot, Group, default));
        Assert.Equal("source_sharing_denied", error.Code);
        Assert.False(error.OutcomeUnknown);
    }

    [Theory]
    [InlineData("missing", "item_missing")]
    [InlineData("id", "item_id")]
    [InlineData("owner", "owner_id")]
    [InlineData("group-owned", "group_owned")]
    [InlineData("group", "group_id")]
    [InlineData("group-mask", "group_mask")]
    [InlineData("base", "base_mask")]
    [InlineData("owner-mask", "owner_mask")]
    [InlineData("everyone", "everyone_mask")]
    [InlineData("next-owner", "next_owner_mask")]
    [InlineData("asset", "asset_id")]
    [InlineData("type", "asset_type")]
    [InlineData("name", "name")]
    public async Task ReadbackMustPreserveIdentitySourceAndUnrelatedPermissions(string mismatch, string field)
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
        var sharing = Sharing((_, _, _) => Task.FromResult(true), (_, _, _) => Task.FromResult(returned));
        var error = await Assert.ThrowsAsync<TaskInventoryException>(() => sharing.PrepareAsync(item, Bot, Group, default));
        Assert.Equal("source_sharing_unconfirmed", error.Code);
        Assert.Contains(": " + field, error.Message);
        Assert.True(error.Retryable);
        Assert.False(error.OutcomeUnknown);
    }

    [Fact]
    public async Task ReportsAllMismatchesWithMaskValuesButNoPrivateIdentityValues()
    {
        var item = Temporary();
        var returned = Prepared(item);
        returned.GroupID = UUID.Random();
        returned.AssetUUID = UUID.Random();
        returned.Name = "Private inventory name";
        returned.Permissions.GroupMask = PermissionMask.None;
        returned.Permissions.EveryoneMask = PermissionMask.Copy;
        var sharing = Sharing((_, _, _) => Task.FromResult(true),
            (_, _, _) => Task.FromResult<InventoryItem?>(returned));

        var error = await Assert.ThrowsAsync<TaskInventoryException>(() => sharing.PrepareAsync(item, Bot, Group, default));

        Assert.Contains("group_id (expected nonzero, observed nonzero)", error.Message);
        Assert.Contains("asset_id (expected nonzero, observed nonzero)", error.Message);
        Assert.Contains("group_mask (expected 0x0008c000, observed 0x00000000)", error.Message);
        Assert.Contains("everyone_mask (expected 0x00000000, observed 0x00008000)", error.Message);
        Assert.Contains("; name.", error.Message);
        foreach (var id in new[] { item.UUID, item.AssetUUID, Bot, Group, UUID.Zero })
            Assert.DoesNotContain(id.ToString(), error.Message);
        Assert.DoesNotContain(item.Name, error.Message);
        Assert.DoesNotContain(returned.Name, error.Message);
        Assert.Contains("Nothing was copied to the target", error.Message);
    }

    [Fact]
    public async Task MutatingTheSdkCachedItemCannotHideAnUnrelatedPermissionChange()
    {
        var item = Temporary();
        var sharing = Sharing((_, _, _) => Task.FromResult(true), (_, _, _) =>
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
        var sharing = Sharing((_, _, _) => Task.FromResult(true), (_, _, _) =>
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
        var sharing = Sharing((_, _, _) => throw new Exception("Must not update"),
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
        Assert.Contains("group_mask (required 0x0008c000, observed 0x00000000, missing 0x0008c000)", error.Message);
        Assert.True(error.OutcomeUnknown);
        Assert.False(error.Retryable);
        TaskInventorySharing.VerifyCopy(copied, UUID.Zero);
    }

    [Fact]
    public void CopyWithAdditionalGroupPermissionsStillPasses()
    {
        var copied = Prepared(Temporary());
        copied.Permissions.GroupMask |= PermissionMask.Transfer;
        TaskInventorySharing.VerifyCopy(copied, Group);
    }

    [Theory]
    [InlineData(AssetType.LSLText)]
    [InlineData(AssetType.Notecard)]
    public async Task HiddenAgentAssetAndUnassignedGroupRequireResolvedUploadIdentityBeforeTaskCopy(AssetType type)
    {
        var item = Temporary(type);
        var cached = Prepared(item);
        cached.GroupID = UUID.Zero;
        cached.AssetUUID = UUID.Zero;
        AssetManager.AssetReceivedCallback? completed = null;
        var reader = new TaskInventoryAssetReader((requested, callback) =>
        {
            Assert.Equal(item.UUID, requested.UUID);
            Assert.Equal(Bot, requested.OwnerID);
            Assert.Equal(UUID.Zero, requested.AssetUUID);
            Assert.Equal(UUID.Zero, requested.GroupID);
            completed = callback;
        });
        var sharing = Sharing((_, _, _) => Task.FromResult(true),
            (_, _, _) => Task.FromResult<InventoryItem?>(cached), reader.ReadAssetIdAsync);

        var pending = sharing.PrepareAsync(item, Bot, Group, default);
        Assert.False(pending.IsCompleted);
        byte[] source = System.Text.Encoding.UTF8.GetBytes("Example source\n");
        Asset asset = type == AssetType.Notecard
            ? new AssetNotecard(item.AssetUUID, TaskInventoryAssetCodec.EncodeNotecard(source))
            : new AssetScriptText(item.AssetUUID, source);
        completed!(new AssetDownload { Success = true, AssetID = item.AssetUUID }, asset);
        var copy = await pending;

        Assert.Equal(item.UUID, copy.UUID);
        Assert.Equal(item.AssetUUID, copy.AssetUUID);
        Assert.Equal(Group, copy.GroupID);
        Assert.Equal(Bot, copy.OwnerID);
        Assert.Equal(cached.Permissions, copy.Permissions);
        Assert.False(copy.GroupOwned);
        Assert.NotSame(cached, copy);
        Assert.Equal(UUID.Zero, cached.AssetUUID);
        Assert.Equal(UUID.Zero, cached.GroupID);
        Assert.Equal(PermissionMask.None, item.Permissions.GroupMask);
        TaskInventorySharing.VerifyCopy(copy, Group);
        var error = Assert.Throws<TaskInventoryException>(() => TaskInventorySharing.VerifyCopy(cached, Group));
        Assert.True(error.OutcomeUnknown);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HiddenAgentAssetCannotUseMissingOrDifferentResolvedIdentity(bool missing)
    {
        var item = Temporary();
        var cached = Prepared(item);
        cached.GroupID = UUID.Zero;
        cached.AssetUUID = UUID.Zero;
        var sharing = Sharing((_, _, _) => Task.FromResult(true),
            (_, _, _) => Task.FromResult<InventoryItem?>(cached),
            (_, _) => Task.FromResult(missing ? UUID.Zero : UUID.Random()));

        var error = await Assert.ThrowsAsync<TaskInventoryException>(() => sharing.PrepareAsync(item, Bot, Group, default));

        Assert.Equal("source_sharing_unconfirmed", error.Code);
        Assert.Contains("asset_id", error.Message);
        Assert.False(error.OutcomeUnknown);
        Assert.Equal(UUID.Zero, cached.AssetUUID);
    }

    [Fact]
    public async Task SourcePermissionFailureCannotAuthorizeTaskCopy()
    {
        var item = Temporary();
        var cached = Prepared(item);
        cached.AssetUUID = UUID.Zero;
        var denied = new TaskInventoryException("source_permission_denied", "Denied");
        var sharing = Sharing((_, _, _) => Task.FromResult(true),
            (_, _, _) => Task.FromResult<InventoryItem?>(cached),
            (_, _) => throw denied);
        Assert.Same(denied, await Assert.ThrowsAsync<TaskInventoryException>(() =>
            sharing.PrepareAsync(item, Bot, Group, default)));
    }

    [Fact]
    public async Task SourceRetrievalCancellationCannotReturnCopyableItem()
    {
        using var cancellation = new CancellationTokenSource();
        var item = Temporary();
        var sharing = Sharing((_, _, _) => Task.FromResult(true),
            (_, _, _) => Task.FromResult<InventoryItem?>(Prepared(item)), (_, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult(item.AssetUUID);
            });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sharing.PrepareAsync(item, Bot, Group, cancellation.Token));
    }

    [Fact]
    public async Task SdkCacheMutationDuringSourceRetrievalCannotChangeTheVerifiedCopy()
    {
        var item = Temporary();
        var cached = Prepared(item);
        cached.GroupID = UUID.Zero;
        cached.AssetUUID = UUID.Zero;
        var sharing = Sharing((_, _, _) => Task.FromResult(true),
            (_, _, _) => Task.FromResult<InventoryItem?>(cached), (_, _) =>
            {
                cached.OwnerID = UUID.Random();
                cached.Permissions.EveryoneMask = PermissionMask.Copy;
                cached.Permissions.GroupMask = PermissionMask.None;
                return Task.FromResult(item.AssetUUID);
            });
        var copy = await sharing.PrepareAsync(item, Bot, Group, default);
        Assert.Equal(Bot, copy.OwnerID);
        Assert.Equal(PermissionMask.None, copy.Permissions.EveryoneMask);
        Assert.Equal(TaskInventorySharing.SharedSourceMask, copy.Permissions.GroupMask);
        Assert.Equal(Group, copy.GroupID);
    }

    private static TaskInventorySharing Sharing(
        Func<UUID, OSDMap, CancellationToken, Task<bool>> update,
        Func<UUID, UUID, CancellationToken, Task<InventoryItem?>> fetch,
        Func<InventoryItem, CancellationToken, Task<UUID>>? resolve = null) =>
        new(update, fetch, resolve ?? ((item, _) => Task.FromResult(item.AssetUUID)));

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

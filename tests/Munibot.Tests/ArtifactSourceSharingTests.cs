using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace Munibot.Tests;

public sealed class ArtifactSourceSharingTests
{
    private static readonly UUID Bot = UUID.Random();
    private static readonly UUID Group = UUID.Random();

    [Fact]
    public async Task PatchesOnlyGroupMaskThenVerifiesExactOpaqueReceiptItem()
    {
        var source = Source();
        source.AssetUUID = UUID.Zero;
        var acknowledged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetches = 0;
        var sharing = new ArtifactSourceSharing((id, patch, _) =>
        {
            Assert.Equal(source.UUID, id);
            Assert.Single(patch);
            var permissions = Assert.IsType<OSDMap>(patch["permissions"]);
            Assert.Single(permissions);
            Assert.Equal(OSDType.Integer, permissions["group_mask"].Type);
            Assert.Equal((uint)TaskInventorySharing.SharedSourceMask,
                permissions["group_mask"].AsUInteger());
            return acknowledged.Task;
        }, (id, owner, _) =>
        {
            fetches++;
            Assert.Equal(source.UUID, id);
            Assert.Equal(Bot, owner);
            return Task.FromResult<InventoryItem?>(Prepared(source));
        });

        var pending = sharing.PrepareAsync(source, Bot, Group, default);
        Assert.False(pending.IsCompleted);
        Assert.Equal(0, fetches);
        acknowledged.SetResult(true);
        var prepared = await pending;

        Assert.Equal(1, fetches);
        Assert.Equal(source.UUID, prepared.UUID);
        Assert.Equal(UUID.Zero, prepared.AssetUUID);
        Assert.Equal(UUID.Zero, prepared.GroupID);
        Assert.Equal(TaskInventorySharing.SharedSourceMask, prepared.Permissions.GroupMask);
        Assert.Equal(PermissionMask.None, source.Permissions.GroupMask);
    }

    [Fact]
    public async Task OpaqueAssetMayBecomeVisibleWithoutBreakingReceiptBinding()
    {
        var source = Source();
        source.AssetUUID = UUID.Zero;
        var visible = UUID.Random();
        var fetched = Prepared(source);
        fetched.AssetUUID = visible;
        var prepared = await Sharing(fetched).PrepareAsync(source, Bot, Group, default);

        Assert.Equal(source.UUID, prepared.UUID);
        Assert.Equal(visible, prepared.AssetUUID);
    }

    [Fact]
    public async Task VisibleAssetIdentityCannotChange()
    {
        var source = Source();
        var fetched = Prepared(source);
        fetched.AssetUUID = UUID.Random();

        var error = await Assert.ThrowsAsync<ArtifactRelayException>(() =>
            Sharing(fetched).PrepareAsync(source, Bot, Group, default));

        Assert.Equal("source_sharing_unconfirmed", error.Code);
        Assert.Contains("asset_id (expected nonzero, observed nonzero)", error.Message);
        Assert.DoesNotContain(source.AssetUUID.ToString(), error.Message);
        Assert.DoesNotContain(fetched.AssetUUID.ToString(), error.Message);
        Assert.True(error.Retryable);
        Assert.False(error.OutcomeUnknown);
    }

    [Theory]
    [InlineData("missing", "item_missing")]
    [InlineData("item", "item_id")]
    [InlineData("owner", "owner_id")]
    [InlineData("group-owned", "group_owned")]
    [InlineData("group", "group_id")]
    [InlineData("group-mask", "group_mask")]
    [InlineData("base", "base_mask")]
    [InlineData("owner-mask", "owner_mask")]
    [InlineData("everyone", "everyone_mask")]
    [InlineData("next-owner", "next_owner_mask")]
    [InlineData("asset-type", "asset_type")]
    [InlineData("inventory-type", "inventory_type")]
    [InlineData("name", "name")]
    [InlineData("description", "description")]
    public async Task ReadbackRejectsReceiptIdentityOrUnrelatedPermissionChange(string mismatch,
        string field)
    {
        var source = Source();
        InventoryItem? fetched = Prepared(source);
        switch (mismatch)
        {
            case "missing": fetched = null; break;
            case "item": fetched.UUID = UUID.Random(); break;
            case "owner": fetched.OwnerID = UUID.Random(); break;
            case "group-owned": fetched.GroupOwned = true; break;
            case "group": fetched.GroupID = UUID.Random(); break;
            case "group-mask": fetched.Permissions.GroupMask = PermissionMask.None; break;
            case "base": fetched.Permissions.BaseMask &= ~PermissionMask.Transfer; break;
            case "owner-mask": fetched.Permissions.OwnerMask &= ~PermissionMask.Transfer; break;
            case "everyone": fetched.Permissions.EveryoneMask |= PermissionMask.Copy; break;
            case "next-owner": fetched.Permissions.NextOwnerMask &= ~PermissionMask.Copy; break;
            case "asset-type": fetched.AssetType = AssetType.Texture; break;
            case "inventory-type": fetched.InventoryType = InventoryType.Texture; break;
            case "name": fetched.Name = "Private changed name"; break;
            case "description": fetched.Description = "Private changed description"; break;
        }

        var error = await Assert.ThrowsAsync<ArtifactRelayException>(() =>
            Sharing(fetched).PrepareAsync(source, Bot, Group, default));

        Assert.Equal("source_sharing_unconfirmed", error.Code);
        Assert.Contains(": " + field, error.Message);
        Assert.Contains("Nothing was copied to the target", error.Message);
        Assert.DoesNotContain(source.UUID.ToString(), error.Message);
        Assert.DoesNotContain(source.Name, error.Message);
    }

    [Fact]
    public async Task RejectedSharingUpdateCannotFetchOrAuthorizeTargetCopy()
    {
        var sharing = new ArtifactSourceSharing((_, _, _) => Task.FromResult(false),
            (_, _, _) => throw new InvalidOperationException("Fetch must not run"));

        var error = await Assert.ThrowsAsync<ArtifactRelayException>(() =>
            sharing.PrepareAsync(Source(), Bot, Group, default));

        Assert.Equal("source_sharing_denied", error.Code);
        Assert.Contains("Nothing was copied to the target", error.Message);
        Assert.False(error.OutcomeUnknown);
    }

    [Fact]
    public async Task AlreadySharedSourceIsStillFetchedAndVerifiedWithoutAnotherPatch()
    {
        var source = Source();
        source.Permissions.GroupMask = TaskInventorySharing.SharedSourceMask;
        var fetched = Prepared(source);
        var sharing = new ArtifactSourceSharing(
            (_, _, _) => throw new InvalidOperationException("Patch must not run"),
            (_, _, _) => Task.FromResult<InventoryItem?>(fetched));

        var prepared = await sharing.PrepareAsync(source, Bot, Group, default);

        Assert.Equal(source.UUID, prepared.UUID);
        Assert.Equal(TaskInventorySharing.SharedSourceMask, prepared.Permissions.GroupMask);
    }

    [Theory]
    [InlineData(PermissionMask.Move)]
    [InlineData(PermissionMask.Modify)]
    [InlineData(PermissionMask.Copy)]
    public async Task SourceMustPermitEveryRequiredGroupGrant(PermissionMask missing)
    {
        var source = Source();
        source.Permissions.BaseMask &= ~missing;
        var sharing = new ArtifactSourceSharing(
            (_, _, _) => throw new InvalidOperationException("Patch must not run"),
            (_, _, _) => throw new InvalidOperationException("Fetch must not run"));

        var error = await Assert.ThrowsAsync<ArtifactRelayException>(() =>
            sharing.PrepareAsync(source, Bot, Group, default));

        Assert.Equal("source_permission_denied", error.Code);
    }

    private static ArtifactSourceSharing Sharing(InventoryItem? fetched) => new(
        (_, _, _) => Task.FromResult(true),
        (_, _, _) => Task.FromResult(fetched));

    private static InventoryItem Source() => new(InventoryType.Object, UUID.Random())
    {
        OwnerID = Bot,
        GroupID = UUID.Zero,
        Name = "Private artifact name",
        Description = "Private artifact description",
        AssetUUID = UUID.Random(),
        AssetType = AssetType.Object,
        InventoryType = InventoryType.Object,
        Permissions = new Permissions(
            (uint)PermissionMask.All,
            0,
            0,
            (uint)PermissionMask.All,
            (uint)PermissionMask.All)
    };

    private static InventoryItem Prepared(InventoryItem source) => new(source.InventoryType, source.UUID)
    {
        ParentUUID = source.ParentUUID,
        OwnerID = source.OwnerID,
        GroupID = UUID.Zero,
        GroupOwned = false,
        Name = source.Name,
        Description = source.Description,
        AssetUUID = source.AssetUUID,
        AssetType = source.AssetType,
        InventoryType = source.InventoryType,
        Permissions = new Permissions(
            (uint)source.Permissions.BaseMask,
            (uint)source.Permissions.EveryoneMask,
            (uint)(source.Permissions.GroupMask | TaskInventorySharing.SharedSourceMask),
            (uint)source.Permissions.NextOwnerMask,
            (uint)source.Permissions.OwnerMask)
    };
}

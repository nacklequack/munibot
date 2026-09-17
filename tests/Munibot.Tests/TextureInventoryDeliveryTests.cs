using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace Munibot.Tests;

public sealed class TextureInventoryDeliveryTests
{
    [Fact]
    public async Task RestrictiveNextOwnerPermissions_ArePatchedAndReadBackBeforeGive()
    {
        var item = Item();
        var calls = new List<string>();
        var delivery = new TextureInventoryDelivery((_, _, _) =>
        {
            calls.Add("fetch");
            return Task.FromResult<InventoryItem?>(item);
        }, (_, patch, _) =>
        {
            calls.Add("update");
            var permissions = Assert.IsType<OSDMap>(patch["permissions"]);
            Assert.Single(permissions);
            Assert.Equal(OSDType.Integer, permissions["next_owner_mask"].Type);
            item.Permissions.NextOwnerMask = (PermissionMask)permissions["next_owner_mask"].AsUInteger();
            return Task.FromResult(true);
        }, (_, _, _, _) => calls.Add("give"));
        await delivery.SendAsync(item.UUID, item.AssetUUID, item.OwnerID, UUID.Random(), default);
        Assert.Equal(new[] { "fetch", "update", "fetch", "give" }, calls);
        Assert.Equal(TextureInventoryDelivery.Required, item.Permissions.NextOwnerMask & TextureInventoryDelivery.Required);
    }

    [Theory]
    [InlineData("asset")]
    [InlineData("owner")]
    [InlineData("type")]
    [InlineData("base")]
    [InlineData("owner-permissions")]
    public async Task WrongOrRestrictedSource_IsNeverGiven(string mismatch)
    {
        var item = Item();
        var asset = item.AssetUUID;
        var owner = item.OwnerID;
        switch (mismatch)
        {
            case "asset": item.AssetUUID = UUID.Random(); break;
            case "owner": item.OwnerID = UUID.Random(); break;
            case "type": item.AssetType = AssetType.Object; break;
            case "base": item.Permissions.BaseMask &= ~PermissionMask.Copy; break;
            case "owner-permissions": item.Permissions.OwnerMask &= ~PermissionMask.Modify; break;
        }
        var delivery = new TextureInventoryDelivery((_, _, _) => Task.FromResult<InventoryItem?>(item),
            (_, _, _) => throw new Exception("Must not patch"), (_, _, _, _) => throw new Exception("Must not give"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => delivery.SendAsync(item.UUID, asset, owner, UUID.Random(), default));
    }

    [Fact]
    public async Task UnconfirmedPermissionUpdate_IsNeverGiven()
    {
        var item = Item();
        var delivery = new TextureInventoryDelivery((_, _, _) => Task.FromResult<InventoryItem?>(item),
            (_, _, _) => Task.FromResult(true), (_, _, _, _) => throw new Exception("Must not give"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => delivery.SendAsync(item.UUID, item.AssetUUID, item.OwnerID, UUID.Random(), default));
    }

    [Fact]
    public async Task ExistingFullPermissions_DoNotNeedUpdate()
    {
        var item = Item();
        item.Permissions.NextOwnerMask = PermissionMask.All;
        var sent = false;
        var delivery = new TextureInventoryDelivery((_, _, _) => Task.FromResult<InventoryItem?>(item),
            (_, _, _) => throw new Exception("Must not patch"), (_, _, _, _) => sent = true);
        await delivery.SendAsync(item.UUID, item.AssetUUID, item.OwnerID, UUID.Random(), default);
        Assert.True(sent);
    }

    private static InventoryItem Item() => new(UUID.Random())
    {
        Name = "Minted texture", AssetUUID = UUID.Random(), OwnerID = UUID.Random(), AssetType = AssetType.Texture,
        Permissions = new() { BaseMask = PermissionMask.All, OwnerMask = PermissionMask.All, NextOwnerMask = PermissionMask.Transfer }
    };
}

using Munibot;
using OpenMetaverse;

namespace Munibot.Tests;

public sealed class InventoryRequestValidatorTests
{
    private const string AvatarId = "11111111-1111-4111-8111-111111111111";
    private const string ItemId = "11111111-2222-3333-4444-555555555555";

    [Fact]
    public void NormalizeAvatarId_ParsesAvatarUuid()
    {
        var parsed = InventoryRequestValidator.NormalizeAvatarId($" {AvatarId} ");

        Assert.Equal(AvatarId, parsed.ToString());
    }

    [Fact]
    public void NormalizeItemId_AllowsMissingItemId()
    {
        Assert.Null(InventoryRequestValidator.NormalizeItemId(null));
        Assert.Null(InventoryRequestValidator.NormalizeItemId(" "));
    }

    [Fact]
    public void NormalizeItemId_ParsesItemUuid()
    {
        var parsed = InventoryRequestValidator.NormalizeItemId($" {ItemId} ");

        Assert.Equal(ItemId, parsed?.ToString());
    }

    [Fact]
    public void NormalizeItemPath_TrimsSlashes()
    {
        var path = InventoryRequestValidator.NormalizeItemPath(" /Textures/Example Poster/ ");

        Assert.Equal("Textures/Example Poster", path);
    }

    [Fact]
    public void NormalizeAssetType_ParsesCaseInsensitiveAssetType()
    {
        var type = InventoryRequestValidator.NormalizeAssetType(" texture ");

        Assert.Equal(AssetType.Texture, type);
    }

    [Theory]
    [InlineData("not-a-uuid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void NormalizeItemId_RejectsInvalidItemUuid(string itemId)
    {
        Assert.Throws<ArgumentException>(() => InventoryRequestValidator.NormalizeItemId(itemId));
    }

    [Fact]
    public void NormalizeItemName_RejectsOverlongItemName()
    {
        var name = new string('x', InventoryRequestValidator.MaxItemNameLength + 1);

        Assert.Throws<ArgumentException>(() => InventoryRequestValidator.NormalizeItemName(name));
    }
}

using System.Text;
using OpenMetaverse;
using OpenMetaverse.Assets;

namespace Munibot.Tests;

public sealed class TaskInventoryAssetReaderTests
{
    private static InventoryItem Item(AssetType type = AssetType.LSLText) => new(UUID.Random())
    {
        AssetUUID = UUID.Zero, AssetType = type, OwnerID = UUID.Random()
    };

    [Theory]
    [InlineData(AssetType.LSLText)]
    [InlineData(AssetType.Notecard)]
    public async Task UnknownAssetIdDownloadsByInventoryIdentityAndReturnsResolvedSource(AssetType type)
    {
        var item = Item(type);
        var assetId = UUID.Random();
        var text = type == AssetType.Notecard ? "BundleId=café\nScript=payload.lsl|1\n" : "default { state_entry() {} }\n";
        var source = Encoding.UTF8.GetBytes(text);
        AssetManager.AssetReceivedCallback? final = null;
        var reader = new TaskInventoryAssetReader((requested, callback) =>
        {
            Assert.Same(item, requested);
            Assert.Equal(UUID.Zero, requested.AssetUUID);
            final = callback;
        });
        var read = reader.ReadAsync(item, default);
        Assert.False(read.IsCompleted);
        Asset asset = type == AssetType.Notecard
            ? new AssetNotecard(assetId, TaskInventoryAssetCodec.EncodeNotecard(source))
            : new AssetScriptText(assetId, source);
        final!(new AssetDownload { Success = true, AssetID = assetId }, asset);
        var result = await read;
        Assert.Equal(assetId, result.AssetId);
        Assert.Equal(source, result.Source);
        Assert.Equal(UUID.Zero, item.AssetUUID);
    }

    [Fact]
    public async Task HiddenAssetIdentityIsFetchedAgainForThePreUpdateCheck()
    {
        var item = Item();
        var first = UUID.Random();
        var second = UUID.Random();
        var ids = new Queue<UUID>([first, second]);
        var reader = new TaskInventoryAssetReader((requested, callback) =>
        {
            Assert.Same(item, requested);
            var id = ids.Dequeue();
            callback(new AssetDownload { Success = true, AssetID = id }, new AssetScriptText(id, [32]));
        });
        Assert.Equal(first, (await reader.ReadAsync(item, default)).AssetId);
        Assert.Equal(second, await reader.ReadAssetIdAsync(item, default));
        Assert.Empty(ids);
    }

    [Fact]
    public async Task DeniedDownloadCannotBecomeSuccessfulReadback()
    {
        var reader = new TaskInventoryAssetReader((_, callback) => callback(new AssetDownload { Success = false }, null));
        var error = await Assert.ThrowsAsync<TaskInventoryException>(() => reader.ReadAsync(Item(), default));
        Assert.Equal("source_unreadable", error.Code);
    }

    [Theory]
    [InlineData(AssetType.LSLText)]
    [InlineData(AssetType.Notecard)]
    public async Task SimulatorPermissionDenialIsNotRetryableAndPreservesRequestDiagnostics(AssetType type)
    {
        var item = Item(type);
        var owner = item.OwnerID;
        var group = UUID.Random();
        item.GroupID = group;
        item.Permissions = new Permissions
        {
            BaseMask = PermissionMask.All, EveryoneMask = PermissionMask.Copy,
            GroupMask = PermissionMask.Copy | PermissionMask.Modify,
            OwnerMask = PermissionMask.Modify | PermissionMask.Copy | PermissionMask.Transfer,
            NextOwnerMask = PermissionMask.All
        };
        var bot = UUID.Random();
        var target = UUID.Random();
        var reader = new TaskInventoryAssetReader((requested, callback) =>
        {
            requested.OwnerID = UUID.Random();
            callback(new AssetDownload { Success = false, Status = StatusCode.InsufficientPermissions }, null);
        }, requested => TaskInventorySourceDiagnosticsDto.Capture(bot, group, target, requested));

        var error = await Assert.ThrowsAsync<TaskInventoryException>(() => reader.ReadAsync(item, default));

        Assert.Equal("source_permission_denied", error.Code);
        Assert.False(error.Retryable);
        Assert.False(error.OutcomeUnknown);
        var details = Assert.IsType<TaskInventorySourceDiagnosticsDto>(error.SourceDiagnostics);
        Assert.Equal(bot.ToString(), details.BotId);
        Assert.Equal(group.ToString(), details.ActiveGroupId);
        Assert.Equal(target.ToString(), details.ObjectId);
        Assert.Equal(item.UUID.ToString(), details.ItemId);
        Assert.Equal(UUID.Zero.ToString(), details.RequestedAssetId);
        Assert.Equal(owner.ToString(), details.ItemOwnerId);
        Assert.Equal(group.ToString(), details.ItemGroupId);
        Assert.Equal(((uint)item.Permissions.GroupMask).ToString("x8"), details.GroupMask);
        Assert.Equal(((uint)item.Permissions.OwnerMask).ToString("x8"), details.OwnerMask);
        Assert.Equal("InsufficientPermissions", details.TransferStatus);
        Assert.False(details.TransferSucceeded);
    }

    [Fact]
    public async Task MissingSourceDataRemainsADistinctRetryableFailure()
    {
        var item = Item();
        var reader = new TaskInventoryAssetReader((_, callback) => callback(
            new AssetDownload { Success = true, Status = StatusCode.OK }, new AssetScriptText(UUID.Random(), [])),
            requested => TaskInventorySourceDiagnosticsDto.Capture(UUID.Random(), UUID.Zero, UUID.Random(), requested));
        var error = await Assert.ThrowsAsync<TaskInventoryException>(() => reader.ReadAsync(item, default));
        Assert.Equal("source_unreadable", error.Code);
        Assert.True(error.Retryable);
        Assert.Equal("OK", error.SourceDiagnostics!.TransferStatus);
        Assert.Equal(UUID.Zero.ToString(), error.SourceDiagnostics.ActiveGroupId);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrapper-mismatch")]
    [InlineData("requested-mismatch")]
    [InlineData("type-mismatch")]
    public async Task UnconfirmedAssetIdentityCannotProduceVerifiedSource(string failure)
    {
        var item = Item();
        var returned = failure == "missing" ? UUID.Zero : UUID.Random();
        if (failure == "requested-mismatch") item.AssetUUID = UUID.Random();
        Asset wrapper = failure == "type-mismatch" ? new AssetNotecard(returned, [32])
            : new AssetScriptText(failure == "wrapper-mismatch" ? UUID.Random() : returned, [32]);
        var reader = new TaskInventoryAssetReader((_, callback) => callback(new AssetDownload { Success = true, AssetID = returned }, wrapper));
        var error = await Assert.ThrowsAsync<TaskInventoryException>(() => reader.ReadAsync(item, default));
        Assert.Equal("source_unconfirmed", error.Code);
    }

    [Fact]
    public async Task CancellationDoesNotTreatAnIncompleteDownloadAsReadback()
    {
        AssetManager.AssetReceivedCallback? final = null;
        var reader = new TaskInventoryAssetReader((_, callback) => final = callback);
        using var cancellation = new CancellationTokenSource();
        var read = reader.ReadAsync(Item(), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        var id = UUID.Random();
        final!(new AssetDownload { Success = true, AssetID = id }, new AssetScriptText(id, [32]));
        Assert.True(read.IsCanceled);
    }
}

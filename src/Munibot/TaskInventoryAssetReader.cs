using System.Text;
using OpenMetaverse;
using OpenMetaverse.Assets;

namespace Munibot;

public sealed record TaskInventorySourceAsset(UUID AssetId, byte[] Source);

public sealed class TaskInventoryAssetReader(Action<InventoryItem, AssetManager.AssetReceivedCallback> request)
{
    public async Task<TaskInventorySourceAsset> ReadAsync(InventoryItem item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<TaskInventorySourceAsset>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Task inventory can hide the asset UUID. The inventory item and task IDs
        // still identify an authorized download, whose reply supplies the asset UUID.
        request(item, (transfer, asset) =>
        {
            if (!transfer.Success || asset?.AssetData is not { Length: > 0 } data)
            {
                completion.TrySetException(new TaskInventoryException("source_unreadable", "The source asset could not be read.", true));
                return;
            }
            var assetId = transfer.AssetID;
            if (assetId == UUID.Zero || asset.AssetID != assetId || asset.AssetType != item.AssetType ||
                (item.AssetUUID != UUID.Zero && item.AssetUUID != assetId))
            {
                completion.TrySetException(new TaskInventoryException("source_unconfirmed", "The source transfer did not confirm the requested asset identity.", true));
                return;
            }
            completion.TrySetResult(new(assetId, data));
        });
        var result = await completion.Task.WaitAsync(ct);
        if (item.AssetType != AssetType.Notecard) return result;
        var notecard = new AssetNotecard(result.AssetId, result.Source);
        if (!notecard.Decode()) throw new TaskInventoryException("invalid_notecard", "The notecard asset could not be decoded.");
        return result with { Source = Encoding.UTF8.GetBytes(notecard.BodyText) };
    }

    public async Task<UUID> ReadAssetIdAsync(InventoryItem item, CancellationToken ct) =>
        item.AssetUUID != UUID.Zero ? item.AssetUUID : (await ReadAsync(item, ct)).AssetId;
}

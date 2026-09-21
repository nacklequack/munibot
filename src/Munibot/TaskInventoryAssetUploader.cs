using OpenMetaverse.StructuredData;

namespace Munibot;

public sealed class TaskInventoryAssetUploader(
    Func<Uri, OSDMap, CancellationToken, Task<(HttpResponseMessage response, byte[] data)>> handshake,
    Func<Uri, byte[], CancellationToken, Task<(HttpResponseMessage response, byte[] data)>> upload)
{
    public async Task<TaskInventoryUploadResult> UploadAsync(Uri capability, OSDMap body, byte[] source,
        bool script, CancellationToken ct)
    {
        // Await both CAPS stages. The SDK convenience method detaches the final upload.
        var first = await handshake(capability, body, ct);
        using var handshakeResponse = first.response;
        if (!handshakeResponse.IsSuccessStatusCode ||
            TaskInventoryAssetCodec.ReadResponse(first.data) is not OSDMap response || response["state"].AsString() != "upload" ||
            !Uri.TryCreate(response["uploader"].AsString(), UriKind.Absolute, out var uploader) || uploader.Scheme != "https")
            throw new TaskInventoryException("upload_rejected", "The simulator rejected the upload handshake.", true);
        var final = await upload(uploader, source, ct);
        using var uploadResponse = final.response;
        if (!uploadResponse.IsSuccessStatusCode)
            throw new TaskInventoryException("upload_unconfirmed", "The simulator did not confirm the final upload. Inspect before retrying.", true, true);
        return TaskInventoryAssetCodec.ReadCompletion(TaskInventoryAssetCodec.ReadResponse(final.data), script);
    }
}

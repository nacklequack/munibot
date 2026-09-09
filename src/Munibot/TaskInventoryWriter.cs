using System.Security.Cryptography;
using System.Text;

namespace Munibot;

public interface ITaskInventoryTarget
{
    Task<IReadOnlyList<TaskInventoryItemDto>> InspectAsync(IReadOnlyList<string> names, CancellationToken cancellationToken);
    Task<TaskInventoryUploadResult> UploadAsync(string name, string contentType, byte[] source,
        TaskInventoryItemDto? existing, CancellationToken cancellationToken);
}

public sealed record TaskInventoryUploadResult(bool Uploaded, bool? Compiled, IReadOnlyList<string> Messages, string? AssetId = null);

public static class TaskInventoryContent
{
    public const int MaxSourceBytes = 256 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static byte[] Normalize(byte[] bytes)
    {
        if (bytes.Length > MaxSourceBytes) throw new ArgumentException("Source exceeds the task inventory size limit.");
        var text = Utf8.GetString(bytes).TrimStart('\uFEFF').Replace("\r\n", "\n").Replace("\r", "\n");
        return Utf8.GetBytes(text.EndsWith('\n') ? text : text + "\n");
    }

    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(Normalize(bytes)));

    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 127 || name != name.Trim() ||
            name is "." or ".." || name.Any(c => char.IsControl(c) || c is '/' or '\\'))
            throw new ArgumentException("An exact inventory name without path separators is required.");
    }

    public static byte[] ValidateWrite(string name, TaskInventoryWriteRequestDto request)
    {
        ValidateName(name);
        if (request.ContentType is not ("script" or "notecard"))
            throw new ArgumentException("ContentType must be script or notecard.");
        if (!Guid.TryParse(request.RequestId, out _)) throw new ArgumentException("RequestId must be a UUID.");
        if (string.IsNullOrEmpty(request.SourceDataBase64) || request.SourceDataBase64.Length > (MaxSourceBytes * 4 / 3) + 4)
            throw new ArgumentException("SourceDataBase64 is missing or exceeds the size limit.");
        byte[] bytes;
        try { bytes = Normalize(Convert.FromBase64String(request.SourceDataBase64)); }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        { throw new ArgumentException("Source must contain base64 encoded UTF-8 text."); }
        if (!string.Equals(Hash(bytes), request.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Source hash does not match ExpectedSha256.");
        return bytes;
    }
}

public sealed class TaskInventoryWriter(ITaskInventoryTarget target)
{
    public async Task<TaskInventoryWriteResultDto> WriteAsync(string name, TaskInventoryWriteRequestDto request, CancellationToken ct)
    {
        var bytes = TaskInventoryContent.ValidateWrite(name, request);
        // Inventory failure must propagate. It is never evidence that an item is missing.
        var existing = Find(await target.InspectAsync([name], ct), name);
        if (existing is not null)
        {
            if (existing.ContentType != request.ContentType)
                throw new TaskInventoryException("wrong_inventory_type", "The inventory name belongs to a different asset type.");
            RequirePermissions(existing, request.ContentType);
        }

        var result = await target.UploadAsync(name, request.ContentType, bytes, existing, ct);
        if (!result.Uploaded || (request.ContentType == "script" && result.Compiled != true))
            return new(request.RequestId, false, result.Uploaded, result.Compiled, result.Messages, null,
                result.Uploaded ? "compile_failed" : "upload_failed", "The inventory upload did not produce verified source.", !result.Uploaded);

        TaskInventoryItemDto? observed = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            observed = Find(await target.InspectAsync([name], ct), name);
            if (observed is not null && observed.ContentType == request.ContentType &&
                (result.AssetId is null || observed.AssetId == result.AssetId) &&
                observed.SourceSha256.Equals(request.ExpectedSha256, StringComparison.OrdinalIgnoreCase) &&
                (request.ContentType != "script" || observed.Running == false))
            {
                RequirePermissions(observed, request.ContentType);
                return new(request.RequestId, true, true, result.Compiled, result.Messages, observed, null, null, false);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
        return new(request.RequestId, false, true, result.Compiled, result.Messages, observed,
            "verification_pending", "The task inventory has not confirmed the uploaded source and stopped state.", true, true);
    }

    public static TaskInventoryItemDto? Find(IReadOnlyList<TaskInventoryItemDto> items, string name)
    {
        var matches = items.Where(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count > 1 || (matches.Count == 1 && matches[0].Name != name))
            throw new TaskInventoryException("ambiguous_inventory_name", "The inventory name is duplicated or differs in case.");
        return matches.SingleOrDefault();
    }

    private static void RequirePermissions(TaskInventoryItemDto item, string contentType)
    {
        if (!item.CanModify || (contentType == "script" && (!item.CanCopy || !item.CanTransfer)))
            throw new TaskInventoryException("permission_denied", "The inventory item lacks modify or distribution permissions.");
    }
}

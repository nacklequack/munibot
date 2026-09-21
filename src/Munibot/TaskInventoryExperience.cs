using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace Munibot;

// Use the same wire contract as the viewer, without caching an item's association.
public sealed class TaskInventoryExperience(
    Func<string, Uri?> capability,
    Func<Uri, CancellationToken, Task<OSD>> get,
    Func<Uri, OSDMap, CancellationToken, Task<OSD>> post)
{
    public async Task PreflightAsync(Guid experienceId, CancellationToken ct)
    {
        var creator = RequireCapability("GetCreatorExperiences");
        RequireCapability("GetMetadata");
        RequireCapability("UpdateScriptTask");
        OSD response;
        try { response = await get(creator, ct); }
        catch (HttpRequestException)
        {
            throw new TaskInventoryException("experience_authority_unconfirmed",
                "The bot's Experience creator authority could not be read.", true);
        }
        if (response is not OSDMap map || map.ContainsKey("error") ||
            !map.TryGetValue("experience_ids", out var value) || value is not OSDArray ids ||
            ids.Any(id => ParseId(id) is null))
            throw new TaskInventoryException("experience_authority_unconfirmed",
                "The simulator did not confirm the bot's Experience creator authority.", true);
        if (!ids.Any(id => ParseId(id) == experienceId))
            throw new TaskInventoryException("experience_permission_denied",
                "The bot is not a creator for the requested Experience.");
    }

    public async Task<Guid?> ReadAsync(UUID objectId, UUID itemId, CancellationToken ct)
    {
        var uri = capability("GetMetadata");
        if (uri is null) return null;
        // Optional inspection must still work on simulators without readable metadata.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var response = await post(uri, new OSDMap
            {
                ["object-id"] = OSD.FromUUID(objectId),
                ["item-id"] = OSD.FromUUID(itemId),
                ["fields"] = new OSDArray { OSD.FromString("experience") }
            }, timeout.Token);
            return response is OSDMap map && !map.ContainsKey("error") &&
                map.TryGetValue("experience", out var experience) ? ParseId(experience) : null;
        }
        catch (HttpRequestException) { return null; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    private Uri RequireCapability(string name) => capability(name)
        ?? throw new TaskInventoryException("capability_unavailable",
            "The simulator does not offer the required Experience capabilities.", true);

    // AsUUID silently converts malformed/missing values to zero. Only an explicit,
    // valid UUID can mean 'no association'; all other metadata remains unknown.
    private static Guid? ParseId(OSD value) =>
        value.Type is OSDType.UUID or OSDType.String && Guid.TryParse(value.AsString(), out var id) ? id : null;
}

using System.Text;
using OpenMetaverse;
using OpenMetaverse.Assets;
using OpenMetaverse.StructuredData;

namespace Munibot;

public static class TaskInventoryAssetCodec
{
    // LibreMetaverse parse exceptions can include raw simulator response excerpts.
    // Turn malformed wire data into an undefined value for the caller to reject.
    public static OSD ReadResponse(byte[] data)
    {
        try
        {
            // The SDK's XML reader silently maps an invalid UUID to zero. Preserve
            // unknown versus explicitly unassociated metadata at the wire boundary.
            var text = Encoding.UTF8.GetString(data).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
            if (text.StartsWith('<'))
            {
                var xml = System.Xml.Linq.XDocument.Parse(text);
                if (xml.Descendants("uuid").Any(node => !string.IsNullOrWhiteSpace(node.Value) &&
                    !Guid.TryParse(node.Value.Trim(), out _))) return new OSD();
            }
            return OSDParser.Deserialize(data);
        }
        catch (Exception ex) when (ex is OSDException or LitJson.JsonException or FormatException or
            System.Xml.XmlException or ArgumentException or EndOfStreamException)
        { return new OSD(); }
    }

    public static OSDMap UploadBody(UUID itemId, UUID? taskId, bool script, Guid? expectedExperienceId)
    {
        var body = new OSDMap { ["item_id"] = OSD.FromUUID(itemId) };
        if (taskId is { } task) body["task_id"] = OSD.FromUUID(task);
        if (script)
        {
            body["target"] = OSD.FromString("mono");
            if (taskId is not null)
            {
                body["is_script_running"] = OSD.FromBoolean(false);
                if (expectedExperienceId is { } experience)
                    body["experience"] = OSD.FromUUID(new UUID(experience));
            }
        }
        return body;
    }

    public static byte[] EncodeNotecard(byte[] source)
    {
        var card = new AssetNotecard { BodyText = Encoding.UTF8.GetString(source) };
        card.Encode();
        return card.AssetData;
    }

    public static TaskInventoryUploadResult ReadCompletion(OSD response, bool script)
    {
        if (response is not OSDMap final || final["state"].AsString() != "complete")
            throw new TaskInventoryException("upload_unconfirmed", "The simulator did not confirm upload completion.", true, true);
        var messages = final.TryGetValue("errors", out var errors) && errors is OSDArray list
            ? list.Select(x => x.AsString()).Take(32).Select(x => x.Length <= 2000 ? x : x[..2000]).ToArray() : [];
        bool? compiled = script ? final["compiled"].AsBoolean() : null;
        var assetId = final["new_asset"].AsUUID();
        // A failed compiler response may omit the new asset. Preserve its diagnostics for the operator.
        if (script && compiled != true) return new(true, false, messages, assetId == UUID.Zero ? null : assetId.ToString());
        if (assetId == UUID.Zero)
            throw new TaskInventoryException("upload_unconfirmed", "The simulator did not return an asset ID.", true, true);
        return new(true, compiled, messages, assetId.ToString());
    }
}

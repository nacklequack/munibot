using System.Text;
using OpenMetaverse;
using OpenMetaverse.Assets;
using OpenMetaverse.StructuredData;

namespace Munibot;

public static class TaskInventoryAssetCodec
{
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

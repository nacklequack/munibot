using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace Munibot;

internal sealed class GridTaskInventoryTarget(GridClient client, Simulator simulator, Primitive primitive) : ITaskInventoryTarget
{
    private readonly TaskInventoryAssetReader sourceReader = new((item, callback) =>
        client.Assets.RequestInventoryAsset(item.AssetUUID, item.UUID, primitive.ID, item.OwnerID,
            item.AssetType, true, UUID.Random(), callback),
        item => TaskInventorySourceDiagnosticsDto.Capture(client.Self.AgentID, client.Self.ActiveGroup, primitive.ID, item));

    public async Task<IReadOnlyList<TaskInventoryItemDto>> InspectAsync(IReadOnlyList<string> names, CancellationToken ct)
    {
        var inventory = await ReadInventoryAsync(ct);
        var results = new List<TaskInventoryItemDto>();
        foreach (var item in inventory.Where(x => names.Contains(x.Name, StringComparer.OrdinalIgnoreCase)))
        {
            var kind = Kind(item.AssetType);
            var modify = Has(item, PermissionMask.Modify);
            var hash = "";
            var assetId = item.AssetUUID;
            bool? running = null;
            if (modify && kind is "script" or "notecard")
            {
                var source = await sourceReader.ReadAsync(item, ct);
                assetId = source.AssetId;
                hash = TaskInventoryContent.Hash(source.Source);
                if (kind == "script") running = await ReadRunningAsync(item.UUID, ct);
            }
            results.Add(new(item.Name, item.UUID.ToString(), assetId.ToString(), kind,
                modify, Has(item, PermissionMask.Copy), Has(item, PermissionMask.Transfer), running, hash));
        }
        return results;
    }

    public async Task<TaskInventoryUploadResult> UploadAsync(string name, string contentType, byte[] source,
        TaskInventoryItemDto? existing, CancellationToken ct)
    {
        var itemId = existing is null ? UUID.Zero : UUID.Parse(existing.ItemId);
        InventoryItem? temporary = null;
        try
        {
            if (existing is null)
            {
                var assetType = contentType == "script" ? AssetType.LSLText : AssetType.Notecard;
                var created = new TaskCompletionSource<InventoryItem>(TaskCreationOptions.RunContinuationsAsynchronously);
                client.Inventory.RequestCreateItem(client.Inventory.FindFolderForType(assetType), name,
                    "Temporary source for task inventory delivery", assetType, UUID.Random(),
                    contentType == "script" ? InventoryType.LSL : InventoryType.Notecard, PermissionMask.All,
                    (success, item) =>
                    {
                        if (success && item is not null) created.TrySetResult(item);
                        else created.TrySetException(new TaskInventoryException("create_failed", "Temporary inventory creation failed.", true));
                    });
                temporary = await created.Task.WaitAsync(ct);
                var agentResult = await UploadAssetAsync(temporary.UUID, contentType, source, false, ct);
                if (!agentResult.Uploaded || (contentType == "script" && agentResult.Compiled != true)) return agentResult;
                temporary.AssetUUID = UUID.Parse(agentResult.AssetId!);
                if (contentType == "script")
                    client.Inventory.CopyScriptToTask(primitive.LocalID, temporary, false, simulator);
                else
                    client.Inventory.UpdateTaskInventory(primitive.LocalID, temporary, simulator);

                // CopyScriptToTask returns a transaction ID, not the new task item's ID.
                for (var attempt = 0; attempt < 40 && itemId == UUID.Zero; attempt++)
                {
                    var matches = (await ReadInventoryAsync(ct)).Where(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (matches.Count > 1 || (matches.Count == 1 && matches[0].Name != name))
                        throw new TaskInventoryException("ambiguous_inventory_name", "The copied inventory name is ambiguous.");
                    if (matches.Count == 1)
                    {
                        if (Kind(matches[0].AssetType) != contentType)
                            throw new TaskInventoryException("wrong_inventory_type", "The copied inventory item has the wrong type.");
                        itemId = matches[0].UUID;
                    }
                    else await Task.Delay(250, ct);
                }
                if (itemId == UUID.Zero)
                    throw new TaskInventoryException("copy_unconfirmed", "The inventory copy has not been confirmed. Inspect before retrying.", true, true);
            }
            else
            {
                var current = (await ReadInventoryAsync(ct)).SingleOrDefault(x => x.UUID == itemId && x.Name == name);
                if (current is null || (await sourceReader.ReadAssetIdAsync(current, ct)).ToString() != existing.AssetId)
                    throw new TaskInventoryException("inventory_changed", "The inventory changed during this operation. Inspect before retrying.", true);
            }
            return await UploadAssetAsync(itemId, contentType, source, true, ct);
        }
        finally
        {
            if (temporary is not null && !ct.IsCancellationRequested)
                await client.Inventory.RemoveItemAsync(temporary.UUID, ct);
        }
    }

    private async Task<List<InventoryItem>> ReadInventoryAsync(CancellationToken ct)
    {
        EnsureSimulator();
        var items = await client.Inventory.GetTaskInventoryAsync(primitive.ID, primitive.LocalID, simulator, ct);
        // LibreMetaverse returns an empty list on cancellation, including during the inventory transfer.
        ct.ThrowIfCancellationRequested();
        var snapshot = items.OfType<InventoryItem>().ToList();
        if (snapshot.Count == 0)
            throw new TaskInventoryException("inventory_unconfirmed",
                "The complete inventory response was empty. Confirm readable object inventory before retrying.", true, true);
        return snapshot;
    }

    private async Task<bool> ReadRunningAsync(UUID itemId, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Callback(object? sender, ScriptRunningReplyEventArgs e)
        {
            if (e.ObjectID == primitive.ID && e.ScriptID == itemId) completion.TrySetResult(e.IsRunning);
        }
        client.Inventory.ScriptRunningReply += Callback;
        try
        {
            client.Inventory.RequestGetScriptRunning(primitive.ID, itemId);
            return await completion.Task.WaitAsync(ct);
        }
        finally { client.Inventory.ScriptRunningReply -= Callback; }
    }

    private async Task<TaskInventoryUploadResult> UploadAssetAsync(UUID itemId, string kind, byte[] source, bool task, CancellationToken ct)
    {
        EnsureSimulator();
        var script = kind == "script";
        var capName = script ? (task ? "UpdateScriptTask" : "UpdateScriptAgent")
            : (task ? "UpdateNotecardTaskInventory" : "UpdateNotecardAgentInventory");
        var capability = simulator.Caps?.CapabilityURI(capName)
            ?? throw new TaskInventoryException("capability_unavailable", "The simulator does not offer the required inventory upload capability.", true);
        var body = new OSDMap { ["item_id"] = OSD.FromUUID(itemId) };
        if (task) body["task_id"] = OSD.FromUUID(primitive.ID);
        if (script)
        {
            body["target"] = OSD.FromString("mono");
            if (task) body["is_script_running"] = OSD.FromBoolean(false);
        }
        else
        {
            source = TaskInventoryAssetCodec.EncodeNotecard(source);
        }

        // Await both CAPS stages directly. The SDK convenience method dispatches the final upload in a detached Task.
        var handshake = await client.HttpCapsClient.PostAsync(capability, OSDFormat.Xml, body, ct);
        using var handshakeResponse = handshake.response;
        if (!handshakeResponse.IsSuccessStatusCode)
            throw new TaskInventoryException("upload_rejected", "The simulator rejected the upload handshake.", true);
        if (OSDParser.Deserialize(handshake.data) is not OSDMap response || response["state"].AsString() != "upload" ||
            !Uri.TryCreate(response["uploader"].AsString(), UriKind.Absolute, out var uploader) || uploader.Scheme != "https")
            throw new TaskInventoryException("upload_rejected", "The simulator rejected the upload handshake.", true);
        var uploaded = await client.HttpCapsClient.PostAsync(uploader, "application/octet-stream", source, ct);
        using var uploadResponse = uploaded.response;
        if (!uploadResponse.IsSuccessStatusCode)
            throw new TaskInventoryException("upload_unconfirmed", "The simulator did not confirm the final upload. Inspect before retrying.", true, true);
        return TaskInventoryAssetCodec.ReadCompletion(OSDParser.Deserialize(uploaded.data), script);
    }

    private void EnsureSimulator()
    {
        if (!client.Network.Connected || client.Network.CurrentSim != simulator)
            throw new TaskInventoryException("simulator_changed", "The simulator connection changed during the operation.", true, true);
    }

    private static bool Has(InventoryItem item, PermissionMask mask) => (item.Permissions.OwnerMask & mask) == mask;
    private static string Kind(AssetType assetType) => assetType switch
    {
        AssetType.LSLText => "script", AssetType.Notecard => "notecard", _ => "unsupported"
    };
}

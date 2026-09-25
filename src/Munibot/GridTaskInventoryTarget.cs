using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace Munibot;

internal sealed class GridTaskInventoryTarget(GridClient client, Simulator simulator, Primitive primitive) : ITaskInventoryTarget
{
    private readonly TaskInventoryExperience experiences = new(
        name => simulator.Caps?.CapabilityURI(name),
        async (uri, ct) =>
        {
            using var response = await client.HttpCapsClient.GetAsync(uri, ct);
            response.EnsureSuccessStatusCode();
            return TaskInventoryAssetCodec.ReadResponse(await response.Content.ReadAsByteArrayAsync(ct));
        },
        async (uri, body, ct) =>
        {
            var result = await client.HttpCapsClient.PostAsync(uri, OSDFormat.Xml, body, ct);
            using var response = result.response;
            response.EnsureSuccessStatusCode();
            return TaskInventoryAssetCodec.ReadResponse(result.data);
        });

    public async Task PreflightExperienceAsync(Guid experienceId, CancellationToken ct)
    {
        EnsureSimulator();
        await experiences.PreflightAsync(experienceId, ct);
        EnsureSimulator();
    }

    private readonly TaskInventoryAssetReader sourceReader = new((item, callback) =>
        client.Assets.RequestInventoryAsset(item.AssetUUID, item.UUID, primitive.ID, item.OwnerID,
            item.AssetType, true, UUID.Random(), callback),
        item => TaskInventorySourceDiagnosticsDto.Capture(client.Self.AgentID, client.Self.ActiveGroup, primitive.ID, item));

    private readonly TaskInventoryAssetReader agentSourceReader = new((item, callback) =>
        client.Assets.RequestInventoryAsset(item.AssetUUID, item.UUID, UUID.Zero, item.OwnerID,
            item.AssetType, true, UUID.Random(), callback),
        item => TaskInventorySourceDiagnosticsDto.Capture(client.Self.AgentID, client.Self.ActiveGroup, UUID.Zero, item));

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
            Guid? experience = null;
            if (modify && kind is "script" or "notecard")
            {
                var source = await sourceReader.ReadAsync(item, ct);
                assetId = source.AssetId;
                hash = TaskInventoryContent.Hash(source.Source);
                if (kind == "script")
                {
                    running = await ReadRunningAsync(item, ct);
                    experience = await experiences.ReadAsync(primitive.ID, item.UUID, ct);
                    EnsureSimulator();
                }
            }
            results.Add(new(item.Name, item.UUID.ToString(), assetId.ToString(), kind,
                modify, Has(item, PermissionMask.Copy), Has(item, PermissionMask.Transfer), running, hash, experience));
        }
        return results;
    }

    public async Task<TaskInventoryUploadResult> UploadAsync(string name, string contentType, byte[] source,
        TaskInventoryItemDto? existing, Guid? expectedExperienceId, CancellationToken ct)
    {
        var itemId = existing is null ? UUID.Zero : UUID.Parse(existing.ItemId);
        InventoryItem? temporary = null;
        var temporaryId = UUID.Zero;
        var targetMayHaveChanged = false;
        try
        {
            if (existing is null)
            {
                var targetProperties = await ReadPropertiesAsync(ct);
                var sourceGroup = TaskInventorySharing.SelectGroup(client.Self.AgentID, client.Self.ActiveGroup, targetProperties);
                if (sourceGroup != UUID.Zero && !client.AisClient.IsAvailable)
                    throw new TaskInventoryException("capability_unavailable", "Verified temporary source sharing requires the inventory API.", true);
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
                temporaryId = temporary.UUID;
                var agentResult = await UploadAssetAsync(temporary.UUID, contentType, source, false, null, ct);
                if (!agentResult.Uploaded || (contentType == "script" && agentResult.Compiled != true)) return agentResult;
                temporary.AssetUUID = UUID.Parse(agentResult.AssetId!);
                temporary = await new TaskInventorySharing(
                    (id, changes, token) => client.AisClient.UpdateItemAsync(id, changes, token),
                    (id, owner, token) => client.Inventory.FetchItemAsync(id, owner, token),
                    agentSourceReader.ReadAssetIdAsync)
                    .PrepareAsync(temporary, client.Self.AgentID, sourceGroup, ct);
                EnsureSimulator();
                targetMayHaveChanged = true;
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
                        TaskInventorySharing.VerifyCopy(matches[0], sourceGroup);
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
            targetMayHaveChanged = true;
            var result = await UploadAssetAsync(itemId, contentType, source, true, expectedExperienceId, ct);
            return result with { ItemId = itemId.ToString() };
        }
        catch (TaskInventoryException ex) when (targetMayHaveChanged && !ex.OutcomeUnknown)
        {
            throw new TaskInventoryException(ex.Code, ex.Message, ex.Retryable, true, ex.SourceDiagnostics);
        }
        finally
        {
            if (temporaryId != UUID.Zero && !ct.IsCancellationRequested)
                await client.Inventory.RemoveItemAsync(temporaryId, ct);
        }
    }

    public async Task<ArtifactTargetRelayResult> RelayArtifactAsync(InventoryItem source,
        ArtifactRelaySpec spec, CancellationToken ct)
    {
        EnsureSimulator();
        VerifyArtifactSource(source);

        var properties = await ReadPropertiesAsync(ct);
        VerifyTargetIdentity(properties, spec);

        UUID sourceGroup;
        try { sourceGroup = TaskInventorySharing.SelectGroup(client.Self.AgentID, client.Self.ActiveGroup, properties); }
        catch (TaskInventoryException ex)
        {
            throw new ArtifactRelayException("target_permission_denied", ex.Message, ex.Retryable, ex.OutcomeUnknown);
        }
        VerifySourceSharing(source, sourceGroup);

        var before = await ReadInventoryAsync(ct);
        VerifyBundle(before, spec.BundleMarkers);
        var prior = FindExactName(before, source.Name);
        if (prior is not null)
        {
            VerifyDelivered(prior, source, spec.ExpectedTargetPermissions, false);
            return new(false, true, prior);
        }

        var delivery = Copy(source);
        delivery.GroupID = sourceGroup;
        client.Inventory.UpdateTaskInventory(primitive.LocalID, delivery, simulator);
        try
        {
            for (var attempt = 0; attempt < 40; attempt++)
            {
                var current = FindExactName(await ReadInventoryAsync(ct), source.Name);
                if (current is not null)
                {
                    VerifyDelivered(current, source, spec.ExpectedTargetPermissions, true);
                    return new(true, false, current);
                }
                await Task.Delay(250, ct);
            }
        }
        catch (TaskInventoryException ex)
        {
            throw new ArtifactRelayException(ex.Code, ex.Message, ex.Retryable, true);
        }
        catch (ArtifactRelayException ex) when (!ex.OutcomeUnknown)
        {
            throw new ArtifactRelayException(ex.Code, ex.Message, ex.Retryable, true);
        }
        throw new ArtifactRelayException("copy_unconfirmed",
            "The exact object copy was not confirmed in target inventory. Inspect before retrying.", true, true);
    }

    private Task<Primitive.ObjectProperties> ReadPropertiesAsync(CancellationToken ct) =>
        new TaskInventoryReadRetry().RunAsync(ReadPropertiesOnceAsync, "object_properties_timeout",
            "Object permission inspection timed out. Inspect the target before retrying.", ct);

    private async Task<Primitive.ObjectProperties> ReadPropertiesOnceAsync(CancellationToken ct)
    {
        EnsureSimulator();
        var completion = new TaskCompletionSource<Primitive.ObjectProperties>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnProperties(object? sender, ObjectPropertiesFamilyEventArgs e)
        {
            if (e.Simulator == simulator && e.Properties.ObjectID == primitive.ID)
                completion.TrySetResult(e.Properties);
        }
        client.Objects.ObjectPropertiesFamily += OnProperties;
        try
        {
            client.Objects.RequestObjectPropertiesFamily(simulator, primitive.ID);
            return await completion.Task.WaitAsync(ct);
        }
        finally { client.Objects.ObjectPropertiesFamily -= OnProperties; }
    }

    private async Task<List<InventoryItem>> ReadInventoryAsync(CancellationToken ct)
    {
        EnsureSimulator();
        var items = await new TaskInventoryReadRetry().RunAsync(
            token => client.Inventory.GetTaskInventoryAsync(primitive.ID, primitive.LocalID, simulator, token),
            "inventory_list_timeout", "The object inventory list timed out. Inspect the target before retrying.", ct);
        // LibreMetaverse returns an empty list on cancellation, including during the inventory transfer.
        ct.ThrowIfCancellationRequested();
        var snapshot = items.OfType<InventoryItem>().ToList();
        if (snapshot.Count == 0)
            throw new TaskInventoryException("inventory_unconfirmed",
                "The complete inventory response was empty. Confirm readable object inventory before retrying.", true, true);
        return snapshot;
    }

    private Task<bool> ReadRunningAsync(InventoryItem item, CancellationToken ct) =>
        new TaskInventoryReadRetry().RunAsync(token => ReadRunningOnceAsync(item.UUID, token), "script_state_timeout",
            $"Script running-state read timed out for {item.Name}. Inspect the target before retrying.", ct);

    private async Task<bool> ReadRunningOnceAsync(UUID itemId, CancellationToken ct)
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

    private async Task<TaskInventoryUploadResult> UploadAssetAsync(UUID itemId, string kind, byte[] source, bool task,
        Guid? expectedExperienceId, CancellationToken ct)
    {
        EnsureSimulator();
        var script = kind == "script";
        var capName = script ? (task ? "UpdateScriptTask" : "UpdateScriptAgent")
            : (task ? "UpdateNotecardTaskInventory" : "UpdateNotecardAgentInventory");
        var capability = simulator.Caps?.CapabilityURI(capName)
            ?? throw new TaskInventoryException("capability_unavailable", "The simulator does not offer the required inventory upload capability.", true);
        var body = TaskInventoryAssetCodec.UploadBody(itemId, task ? primitive.ID : null, script, expectedExperienceId);
        if (!script)
        {
            source = TaskInventoryAssetCodec.EncodeNotecard(source);
        }

        return await new TaskInventoryAssetUploader(
            (uri, request, token) => client.HttpCapsClient.PostAsync(uri, OSDFormat.Xml, request, token),
            (uri, bytes, token) => client.HttpCapsClient.PostAsync(uri, "application/octet-stream", bytes, token))
            .UploadAsync(capability, body, source, script, ct);
    }

    private void EnsureSimulator()
    {
        if (!client.Network.Connected || client.Network.CurrentSim != simulator)
            throw new TaskInventoryException("simulator_changed", "The simulator connection changed during the operation.", true, true);
    }

    private static bool Has(InventoryItem item, PermissionMask mask) => (item.Permissions.OwnerMask & mask) == mask;

    internal static void VerifyArtifactSource(InventoryItem source)
    {
        if (source.AssetType != AssetType.Object || source.InventoryType != InventoryType.Object ||
            source.AssetUUID == UUID.Zero)
            throw new ArtifactRelayException("source_type_mismatch",
                "The verified source is not an exact object inventory item.");
        if (!Has(source, PermissionMask.Copy) || !Has(source, PermissionMask.Transfer))
            throw new ArtifactRelayException("source_permission_denied",
                "The source object must remain copyable and transferable.");
    }

    internal static void VerifyTargetIdentity(Primitive.ObjectProperties properties, ArtifactRelaySpec spec)
    {
        if (properties.ObjectID != spec.TargetObjectId || properties.Name != spec.TargetName ||
            properties.OwnerID != spec.TargetOwnerId || properties.GroupID != spec.TargetGroupId)
            throw new ArtifactRelayException("target_identity_mismatch",
                "The visible object does not match the exact managed scanner identity.");
    }

    internal static void VerifySourceSharing(InventoryItem source, UUID sourceGroup)
    {
        if (sourceGroup != UUID.Zero &&
            (source.Permissions.GroupMask & TaskInventorySharing.SharedSourceMask) != TaskInventorySharing.SharedSourceMask)
            throw new ArtifactRelayException("source_permission_denied",
                "The source object does not carry the group sharing required by the managed scanner.");
    }

    internal static void VerifyBundle(IReadOnlyList<InventoryItem> inventory,
        IReadOnlyList<ArtifactBundleMarkerSpec> markers)
    {
        foreach (var marker in markers)
        {
            var matches = inventory.Where(x => x.Name.Equals(marker.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1 || matches[0].Name != marker.Name || matches[0].AssetType != marker.AssetType ||
                matches[0].AssetUUID != marker.AssetId)
                throw new ArtifactRelayException("bundle_mismatch",
                    "The target inventory did not match the exact managed bundle markers.");
        }
    }

    internal static InventoryItem? FindExactName(IReadOnlyList<InventoryItem> inventory, string name)
    {
        var matches = inventory.Where(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count > 1 || matches.Count == 1 && matches[0].Name != name)
            throw new ArtifactRelayException("target_inventory_conflict",
                "The target already contains an ambiguous artifact name.");
        return matches.SingleOrDefault();
    }

    internal static void VerifyDelivered(InventoryItem target, InventoryItem source,
        InventoryPermissionMasksDto expectedPermissions, bool outcomeUnknown)
    {
        if (target.Name != source.Name || target.AssetType != AssetType.Object ||
            target.InventoryType != InventoryType.Object || target.AssetUUID != source.AssetUUID ||
            !expectedPermissions.Matches(target.Permissions))
            throw new ArtifactRelayException("target_receipt_mismatch",
                "The target object copy did not match the expected identity and permissions.", false, outcomeUnknown);
    }

    private static InventoryItem Copy(InventoryItem item) => new(item.InventoryType, item.UUID)
    {
        ParentUUID = item.ParentUUID,
        Name = item.Name,
        OwnerID = item.OwnerID,
        AssetUUID = item.AssetUUID,
        Permissions = item.Permissions,
        AssetType = item.AssetType,
        CreatorID = item.CreatorID,
        Description = item.Description,
        GroupID = item.GroupID,
        GroupOwned = item.GroupOwned,
        SalePrice = item.SalePrice,
        SaleType = item.SaleType,
        Flags = item.Flags,
        CreationDate = item.CreationDate,
        TransactionID = item.TransactionID,
        LastOwnerID = item.LastOwnerID
    };

    private static string Kind(AssetType assetType) => assetType switch
    {
        AssetType.LSLText => "script",
        AssetType.Notecard => "notecard",
        _ => "unsupported"
    };
}

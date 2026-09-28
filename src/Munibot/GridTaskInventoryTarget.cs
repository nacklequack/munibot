using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace Munibot;

internal sealed class GridTaskInventoryTarget(
    GridClient client,
    Simulator simulator,
    Primitive primitive,
    ILogger logger) : ITaskInventoryTarget
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
        ArtifactRelaySpec spec, Guid requestId, CancellationToken ct)
    {
        EnsureSimulator();
        VerifyArtifactSource(source);
        VerifyTargetPermissionPolicy(source, spec.ExpectedTargetPermissions);

        var properties = await ReadPropertiesAsync(ct);
        VerifyTargetIdentity(properties, spec);

        UUID sourceGroup;
        try { sourceGroup = TaskInventorySharing.SelectGroup(client.Self.AgentID, client.Self.ActiveGroup, properties); }
        catch (TaskInventoryException ex)
        {
            throw new ArtifactRelayException("target_permission_denied", ex.Message, ex.Retryable, ex.OutcomeUnknown);
        }
        var sourceNeedsPreparation = source.Description != spec.DeliveryMarker ||
            (uint)source.Permissions.NextOwnerMask != spec.ExpectedTargetPermissions.NextOwner ||
            sourceGroup != UUID.Zero &&
            (source.Permissions.GroupMask & TaskInventorySharing.SharedSourceMask) !=
            TaskInventorySharing.SharedSourceMask;
        if (sourceNeedsPreparation && !client.AisClient.IsAvailable)
            throw new ArtifactRelayException("capability_unavailable",
                "Verified received-artifact delivery preparation requires the inventory API.", true);
        source = await new ArtifactSourceSharing(
                (id, changes, token) => client.AisClient.UpdateItemAsync(id, changes, token),
                (id, owner, token) => client.Inventory.FetchItemAsync(id, owner, token))
            .PrepareAsync(source, client.Self.AgentID, sourceGroup, spec.DeliveryMarker,
                spec.ExpectedTargetPermissions, ct);
        VerifySourceSharing(source, sourceGroup);
        logger.LogInformation(
            "Artifact source delivery prepared; requestId={RequestId} sharing={Sharing} assetIdentity={AssetIdentity} marker=verified permissions=verified targetMutation=not-started",
            requestId, sourceGroup == UUID.Zero ? "not-required" : "active-group",
            source.AssetUUID == UUID.Zero ? "opaque" : "visible");

        var before = await ReadInventoryAsync(ct);
        await VerifyBundleAsync(before, spec.BundleMarkers, requestId, ct);
        var prior = FindExactName(before, source.Name);
        if (prior is not null)
        {
            VerifyDelivered(prior, source, spec.DeliveryMarker, spec.ExpectedTargetPermissions, false);
            return new(false, true, source, prior);
        }

        var delivery = PrepareDelivery(source, spec.DeliveryMarker, spec.ExpectedTargetPermissions);
        delivery.GroupID = sourceGroup;
        client.Inventory.UpdateTaskInventory(primitive.LocalID, delivery, simulator);
        try
        {
            for (var attempt = 0; attempt < 40; attempt++)
            {
                var current = FindExactName(await ReadInventoryAsync(ct), source.Name);
                if (current is not null)
                {
                    VerifyDelivered(current, source, spec.DeliveryMarker, spec.ExpectedTargetPermissions, true);
                    return new(true, false, source, current);
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
        throw new ArtifactRelayException("target_write_unconfirmed",
            "The scanner did not report the exact object copy after the target write. Inspect its inventory before retrying.",
            true, true);
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
        if (source.AssetType != AssetType.Object || source.InventoryType != InventoryType.Object)
            throw new ArtifactRelayException("source_type_mismatch",
                "The verified source is not an exact object inventory item.");
        if (!Has(source, PermissionMask.Modify) || !Has(source, PermissionMask.Copy) ||
            !Has(source, PermissionMask.Transfer))
            throw new ArtifactRelayException("source_permission_denied",
                "The source object must remain modifiable, copyable and transferable.");
    }

    internal static void VerifyTargetPermissionPolicy(InventoryItem source,
        InventoryPermissionMasksDto target)
    {
        var copyOnly = (uint)(PermissionMask.Move | PermissionMask.Copy);
        if (target.Base != (uint)source.Permissions.BaseMask || target.Owner != copyOnly || target.Group != 0
            || target.Everyone != 0 || target.NextOwner != copyOnly)
            throw new ArtifactRelayException("target_permission_policy_invalid",
                "Scanner artifacts must be installed copy-only, without modify or transfer permission.");
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

    private async Task VerifyBundleAsync(IReadOnlyList<InventoryItem> inventory,
        IReadOnlyList<ArtifactBundleMarkerSpec> markers, Guid requestId, CancellationToken ct)
    {
        for (var index = 0; index < markers.Count; index++)
        {
            var marker = markers[index];
            var match = MatchBundleMarker(inventory, marker);
            if (match.Mismatches.Count != 0)
            {
                LogBundleFailure(requestId, index + 1, "bundle_mismatch", match.Mismatches);
                throw BundleMismatch(match.Mismatches);
            }
            if (marker.ScriptProof is not { } proof)
            {
                logger.LogInformation(
                    "Artifact bundle marker verified; requestId={RequestId} markerIndex={MarkerIndex} identityMode=exact fields={Fields} targetMutation=not-started",
                    requestId, index + 1, "count,name,asset_type,asset_identity");
                continue;
            }

            TaskInventorySourceAsset source;
            bool running;
            Guid? experience;
            var field = "source_hash";
            try
            {
                source = await sourceReader.ReadAsync(match.Item!, ct);
                field = "running";
                running = await ReadRunningAsync(match.Item!, ct);
                field = "experience";
                experience = await experiences.ReadAsync(primitive.ID, match.Item!.UUID, ct);
                EnsureSimulator();
            }
            catch (TaskInventoryException ex)
            {
                LogBundleFailure(requestId, index + 1, "bundle_unconfirmed", [field]);
                throw new ArtifactRelayException("bundle_unconfirmed",
                    $"The managed bundle marker {field.Replace('_', ' ')} could not be verified before target mutation.",
                    ex.Retryable);
            }

            IReadOnlyList<string> mismatches;
            try { mismatches = ScriptProofMismatches(proof, source.Source, running, experience); }
            catch (ArgumentException)
            {
                LogBundleFailure(requestId, index + 1, "bundle_mismatch", ["source_hash"]);
                throw BundleMismatch(["source_hash"]);
            }
            if (mismatches.Count != 0)
            {
                var unconfirmed = mismatches.Contains("experience_unconfirmed");
                LogBundleFailure(requestId, index + 1, unconfirmed ? "bundle_unconfirmed" : "bundle_mismatch",
                    mismatches);
                if (unconfirmed)
                    throw new ArtifactRelayException("bundle_unconfirmed",
                        "The managed bundle marker Experience association could not be verified before target mutation.",
                        true);
                throw BundleMismatch(mismatches);
            }

            var identityMode = match.Item!.AssetUUID == marker.AssetId || source.AssetId == marker.AssetId
                ? "exact" : "script-proof";
            logger.LogInformation(
                "Artifact bundle marker verified; requestId={RequestId} markerIndex={MarkerIndex} identityMode={IdentityMode} fields={Fields} targetMutation=not-started",
                requestId, index + 1, identityMode,
                "count,name,asset_type,inventory_type,permissions,source_hash,running,experience");
        }
    }

    internal static ArtifactBundleMarkerMatch MatchBundleMarker(IReadOnlyList<InventoryItem> inventory,
        ArtifactBundleMarkerSpec marker)
    {
        var matches = inventory.Where(item =>
            item.Name.Equals(marker.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        var mismatches = new List<string>();
        if (matches.Count != 1)
        {
            mismatches.Add("inventory_count");
            return new(null, mismatches);
        }

        var item = matches[0];
        if (item.Name != marker.Name) mismatches.Add("inventory_name");
        if (item.AssetType != marker.AssetType) mismatches.Add("asset_type");
        if (marker.ScriptProof is { } proof)
        {
            if (item.InventoryType != proof.InventoryType) mismatches.Add("inventory_type");
            if (!proof.Permissions.Matches(item.Permissions)) mismatches.Add("permissions");
        }
        else if (item.AssetUUID != marker.AssetId) mismatches.Add("asset_identity");
        return new(item, mismatches);
    }

    internal static IReadOnlyList<string> ScriptProofMismatches(ArtifactBundleScriptProofSpec proof,
        byte[] source, bool running, Guid? experience)
    {
        var mismatches = new List<string>();
        if (!TaskInventoryContent.Hash(source).Equals(proof.SourceSha256, StringComparison.Ordinal))
            mismatches.Add("source_hash");
        if (running != proof.Running) mismatches.Add("running");
        if (experience is null) mismatches.Add("experience_unconfirmed");
        else if (experience.Value != proof.ExperienceId) mismatches.Add("experience");
        return mismatches;
    }

    private void LogBundleFailure(Guid requestId, int markerIndex, string code,
        IReadOnlyList<string> fields) => logger.LogWarning(
        "Artifact bundle marker verification failed; requestId={RequestId} markerIndex={MarkerIndex} code={Code} fields={Fields} targetMutation=not-started",
        requestId, markerIndex, code, string.Join(',', fields));

    private static ArtifactRelayException BundleMismatch(IReadOnlyList<string> fields) => new(
        "bundle_mismatch",
        $"The target inventory did not match the managed bundle marker fields: {string.Join(", ", fields)}.");

    internal static InventoryItem? FindExactName(IReadOnlyList<InventoryItem> inventory, string name)
    {
        var matches = inventory.Where(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count > 1 || matches.Count == 1 && matches[0].Name != name)
            throw new ArtifactRelayException("target_inventory_conflict",
                "The target already contains an ambiguous artifact name.");
        return matches.SingleOrDefault();
    }

    internal static void VerifyDelivered(InventoryItem target, InventoryItem source, string deliveryMarker,
        InventoryPermissionMasksDto expectedPermissions, bool outcomeUnknown)
    {
        var mismatches = new List<string>();
        if (target.Name != source.Name) mismatches.Add("inventory name");
        if (target.Description != deliveryMarker) mismatches.Add("delivery marker");
        if (target.AssetType != AssetType.Object) mismatches.Add("asset type");
        if (target.InventoryType != InventoryType.Object) mismatches.Add("inventory type");
        mismatches.AddRange(expectedPermissions.TransferredReceiptMismatches(target.Permissions));
        if (mismatches.Count != 0)
            throw new ArtifactRelayException("target_receipt_mismatch",
                $"The target object copy did not match the expected {string.Join(", ", mismatches)}.",
                false, outcomeUnknown);
    }

    internal static InventoryItem PrepareDelivery(InventoryItem item, string deliveryMarker,
        InventoryPermissionMasksDto targetPermissions) => new(item.InventoryType, item.UUID)
        {
            ParentUUID = item.ParentUUID,
            Name = item.Name,
            OwnerID = item.OwnerID,
            AssetUUID = item.AssetUUID,
            // The source must remain transferable while Rosalind owns and inserts it. Second Life
            // applies NextOwnerMask when ownership changes to the scanner owner, producing the
            // requested copy-only task-inventory item after the transfer.
            Permissions = new Permissions(targetPermissions.Base, targetPermissions.Everyone,
            targetPermissions.Group, targetPermissions.Owner, (uint)item.Permissions.OwnerMask),
            AssetType = item.AssetType,
            CreatorID = item.CreatorID,
            Description = deliveryMarker,
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

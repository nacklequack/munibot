using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenMetaverse.Messages.Linden;
using OpenMetaverse.Packets;

namespace Munibot;

public sealed class SecondLifeBotSession(
    BotConfig config,
    ILogger<SecondLifeBotSession> logger,
    ISecondLifeAccountHistoryClient accountHistoryClient,
    IWalletEventPublisher walletEventPublisher) : IAsyncDisposable
{
    private readonly GridClient _client = new();
    private readonly SemaphoreSlim _groupRosterLock = new(1, 1);
    private readonly SemaphoreSlim _groupOperationLock = new(1, 1);
    private readonly SemaphoreSlim _teleportLock = new(1, 1);
    private readonly SemaphoreSlim _avatarLookupLock = new(1, 1);
    private readonly SemaphoreSlim _peopleSearchLock = new(1, 1);
    private readonly SemaphoreSlim _experienceLock = new(1, 1);
    private readonly SemaphoreSlim _inventoryLock = new(1, 1);
    private readonly SemaphoreSlim _walletLock = new(1, 1);
    private readonly SemaphoreSlim _walletHistoryReconcileLock = new(1, 1);
    private readonly object _walletBalanceStateLock = new();
    private readonly object _walletEventTransactionLock = new();
    private readonly HashSet<string> _deliveredWalletEventTransactionIds = new(StringComparer.OrdinalIgnoreCase);
    private int? _lastObservedWalletBalance;
    private bool _eventsWired;

    public bool IsOnline => _client.Network.Connected;
    public string? CurrentSimulator => _client.Network.CurrentSim?.Name;
    public string AgentId => _client.Self.AgentID.ToString();
    public string? LastDisconnectReason { get; private set; }

    public BotLocationDto GetLocation()
    {
        var position = _client.Network.Connected
            ? new Vector3Dto(_client.Self.SimPosition.X, _client.Self.SimPosition.Y, _client.Self.SimPosition.Z)
            : null;

        return new BotLocationDto(
            _client.Network.Connected,
            _client.Network.Connected ? _client.Self.AgentID.ToString() : null,
            _client.Network.CurrentSim?.Name,
            position,
            DateTimeOffset.UtcNow);
    }

    public async Task LoginAsync(CancellationToken cancellationToken)
    {
        WireEvents();

        var login = config.Login;
        var loginParams = _client.Network.DefaultLoginParams(
            login.FirstName,
            login.LastName,
            login.Password,
            login.Channel,
            login.Version);

        loginParams.Start = login.Start;
        loginParams.Timeout = (int)TimeSpan.FromSeconds(login.TimeoutSeconds).TotalMilliseconds;

        if (!string.IsNullOrWhiteSpace(login.LoginUri))
        {
            loginParams.URI = login.LoginUri;
        }

        if (!string.IsNullOrWhiteSpace(login.MfaToken))
        {
            loginParams.Token = login.MfaToken;
        }

        if (!string.IsNullOrWhiteSpace(login.MfaHash))
        {
            loginParams.MfaHash = login.MfaHash;
        }

        logger.LogInformation(
            "Logging in as {FirstName} {LastName} using channel {Channel} {Version}",
            login.FirstName,
            login.LastName,
            login.Channel,
            login.Version);

        var success = await _client.Network.LoginAsync(loginParams, cancellationToken);
        if (!success)
        {
            throw new InvalidOperationException(
                $"Login failed: {_client.Network.LoginMessage} ({_client.Network.LoginErrorKey})");
        }

        logger.LogInformation(
            "Logged in as agent {AgentId}; current simulator={Simulator}; position={Position}",
            _client.Self.AgentID,
            _client.Network.CurrentSim?.Name ?? "unknown",
            _client.Self.SimPosition);

        ConfigureMovementKeepalive();
        SendMovementKeepalive();
        await AllowConfiguredExperiencesAsync(cancellationToken);
        LastDisconnectReason = null;
    }

    public void Logout()
    {
        if (!_client.Network.Connected)
        {
            return;
        }

        logger.LogInformation("Logging out");
        _client.Network.Logout();
    }

    private void ConfigureMovementKeepalive()
    {
        if (config.Runtime.MovementKeepaliveSeconds == 0)
        {
            _client.Self.Movement.UpdateInterval = 0;
            logger.LogInformation("Movement keepalive is disabled.");
            return;
        }

        _client.Self.Movement.AutoResetControls = true;
        _client.Self.Movement.UpdateInterval =
            (int)TimeSpan.FromSeconds(config.Runtime.MovementKeepaliveSeconds).TotalMilliseconds;

        logger.LogInformation(
            "Movement keepalive enabled; sending AgentUpdate every {IntervalSeconds} second(s).",
            config.Runtime.MovementKeepaliveSeconds);
    }

    public bool SendMovementKeepalive()
    {
        if (config.Runtime.MovementKeepaliveSeconds == 0 || !_client.Network.Connected)
        {
            return false;
        }

        _client.Self.Movement.SendUpdate(false);
        logger.LogDebug("Movement keepalive AgentUpdate sent.");
        return true;
    }

    public async Task<GroupRosterDto> GetGroupRosterAsync(string groupUuid, CancellationToken cancellationToken)
    {
        var groupId = GroupRequestValidator.NormalizeGroupId(groupUuid);

        if (!_client.Network.Connected)
        {
            throw new InvalidOperationException("Munibot is not logged in.");
        }

        await _groupRosterLock.WaitAsync(cancellationToken);
        try
        {
            var timeout = TimeSpan.FromSeconds(config.Api.GroupRosterTimeoutSeconds);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            var tcs = new TaskCompletionSource<GroupMembersReplyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            UUID requestId = UUID.Zero;

            EventHandler<GroupMembersReplyEventArgs>? handler = null;
            handler = (_, e) =>
            {
                if (e.RequestID == requestId)
                {
                    tcs.TrySetResult(e);
                }
            };

            try
            {
                _client.Groups.GroupMembersReply += handler;

                var requestedAt = DateTimeOffset.UtcNow;
                requestId = _client.Groups.RequestGroupMembers(groupId);

                await using var _ = timeoutCts.Token.Register(() =>
                    tcs.TrySetCanceled(timeoutCts.Token));

                var reply = await tcs.Task.ConfigureAwait(false);
                var members = reply.Members
                    .OrderBy(member => member.Key.ToString(), StringComparer.OrdinalIgnoreCase)
                    .Select(member => ToMemberDto(member.Key, member.Value))
                    .ToList();

                logger.LogInformation(
                    "Fetched group roster for {GroupUuid}: requestId={RequestId} members={MemberCount}",
                    groupId,
                    requestId,
                    members.Count);

                return new GroupRosterDto(
                    groupId.ToString(),
                    requestId.ToString(),
                    members.Count,
                    requestedAt,
                    DateTimeOffset.UtcNow,
                    members);
            }
            finally
            {
                _client.Groups.GroupMembersReply -= handler;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out waiting for Second Life group roster after {config.Api.GroupRosterTimeoutSeconds} seconds.");
        }
        finally
        {
            _groupRosterLock.Release();
        }
    }

    public async Task<GroupBanListDto> GetGroupBansAsync(string groupUuid, CancellationToken cancellationToken)
    {
        var groupId = GroupRequestValidator.NormalizeGroupId(groupUuid);
        EnsureOnline();

        var timeout = TimeSpan.FromSeconds(config.Api.GroupOperationTimeoutSeconds);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var requestedAt = DateTimeOffset.UtcNow;
        BannedAgentsEventArgs? reply = null;

        await _client.Groups.RequestBannedAgents(
            groupId,
            (_, e) => reply = e,
            timeoutCts.Token);

        if (reply is null)
        {
            throw new TimeoutException(
                $"Timed out waiting for Second Life group ban list after {config.Api.GroupOperationTimeoutSeconds} seconds.");
        }

        if (!reply.Success)
        {
            throw new InvalidOperationException($"Second Life group ban list request failed for group '{groupId}'.");
        }

        var bans = (reply.BannedAgents ?? new Dictionary<UUID, DateTime>())
            .OrderBy(entry => entry.Key.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(entry => new GroupBanEntryDto(entry.Key.ToString(), entry.Value))
            .ToList();

        return new GroupBanListDto(
            groupId.ToString(),
            bans.Count,
            requestedAt,
            DateTimeOffset.UtcNow,
            bans);
    }

    public Task<GroupOperationResultDto> UnbanGroupMemberAsync(
        string groupUuid,
        string avatarUuid,
        CancellationToken cancellationToken)
        => ExecuteGroupBanActionAsync(groupUuid, avatarUuid, GroupBanAction.Unban, "unban", cancellationToken);

    public async Task<GroupOperationResultDto> InviteGroupMemberAsync(
        string groupUuid,
        GroupInviteRequestDto request,
        CancellationToken cancellationToken)
    {
        var groupId = GroupRequestValidator.NormalizeGroupId(groupUuid);
        var avatarId = GroupRequestValidator.NormalizeAvatarId(request.AvatarId);
        var roleIds = GroupRequestValidator.NormalizeRoleIds(request.RoleIds);
        EnsureOnline();

        await _groupOperationLock.WaitAsync(cancellationToken);
        try
        {
            var requestedAt = DateTimeOffset.UtcNow;
            _client.Groups.Invite(groupId, roleIds.ToList(), avatarId);

            logger.LogInformation(
                "Issued group invite for avatar {AvatarId} to group {GroupId} roles={RoleCount}",
                avatarId,
                groupId,
                roleIds.Count);

            return new GroupOperationResultDto(
                groupId.ToString(),
                avatarId.ToString(),
                "invite",
                true,
                requestedAt,
                DateTimeOffset.UtcNow);
        }
        finally
        {
            _groupOperationLock.Release();
        }
    }

    public async Task<GroupOperationResultDto> EjectGroupMemberAsync(
        string groupUuid,
        string avatarUuid,
        CancellationToken cancellationToken)
    {
        var groupId = GroupRequestValidator.NormalizeGroupId(groupUuid);
        var avatarId = GroupRequestValidator.NormalizeAvatarId(avatarUuid);
        EnsureOnline();

        await _groupOperationLock.WaitAsync(cancellationToken);
        try
        {
            var timeout = TimeSpan.FromSeconds(config.Api.GroupOperationTimeoutSeconds);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            var requestedAt = DateTimeOffset.UtcNow;
            var tcs = new TaskCompletionSource<GroupOperationEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

            EventHandler<GroupOperationEventArgs>? handler = null;
            handler = (_, e) =>
            {
                if (e.GroupID == groupId)
                {
                    tcs.TrySetResult(e);
                }
            };

            try
            {
                _client.Groups.GroupMemberEjected += handler;
                _client.Groups.EjectUser(groupId, avatarId);

                await using var _ = timeoutCts.Token.Register(() =>
                    tcs.TrySetCanceled(timeoutCts.Token));

                var reply = await tcs.Task.ConfigureAwait(false);
                if (!reply.Success)
                {
                    throw new InvalidOperationException($"Second Life group eject failed for avatar '{avatarId}'.");
                }

                return new GroupOperationResultDto(
                    groupId.ToString(),
                    avatarId.ToString(),
                    "eject",
                    true,
                    requestedAt,
                    DateTimeOffset.UtcNow);
            }
            finally
            {
                _client.Groups.GroupMemberEjected -= handler;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out waiting for Second Life group eject after {config.Api.GroupOperationTimeoutSeconds} seconds.");
        }
        finally
        {
            _groupOperationLock.Release();
        }
    }

    public async Task<GroupMemberRolesDto> GetGroupMemberRolesAsync(
        string groupUuid,
        string avatarUuid,
        CancellationToken cancellationToken)
    {
        var groupId = GroupRequestValidator.NormalizeGroupId(groupUuid);
        var avatarId = GroupRequestValidator.NormalizeAvatarId(avatarUuid);
        EnsureOnline();

        var requestedAt = DateTimeOffset.UtcNow;
        var roster = await GetGroupRosterAsync(groupUuid, cancellationToken);
        var isMember = roster.Members.Any(member =>
            string.Equals(member.AvatarId, avatarId.ToString(), StringComparison.OrdinalIgnoreCase));

        if (!isMember)
        {
            return new GroupMemberRolesDto(
                groupId.ToString(),
                avatarId.ToString(),
                Array.Empty<string>(),
                requestedAt,
                DateTimeOffset.UtcNow);
        }

        var roles = await GetGroupRolesByIdAsync(groupId, cancellationToken);
        var roleMembers = await GetGroupRoleMembersAsync(groupId, cancellationToken);
        var roleNames = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { "Everyone" };

        foreach (var pair in roleMembers)
        {
            UUID? roleId = null;
            if (pair.Value == avatarId)
            {
                roleId = pair.Key;
            }
            else if (pair.Key == avatarId)
            {
                roleId = pair.Value;
            }

            if (roleId.HasValue && roles.TryGetValue(roleId.Value, out var role) && !string.IsNullOrWhiteSpace(role.Name))
            {
                roleNames.Add(role.Name);
            }
        }

        return new GroupMemberRolesDto(
            groupId.ToString(),
            avatarId.ToString(),
            roleNames.ToList(),
            requestedAt,
            DateTimeOffset.UtcNow);
    }

    private async Task<GroupOperationResultDto> ExecuteGroupBanActionAsync(
        string groupUuid,
        string avatarUuid,
        GroupBanAction action,
        string operation,
        CancellationToken cancellationToken)
    {
        var groupId = GroupRequestValidator.NormalizeGroupId(groupUuid);
        var avatarId = GroupRequestValidator.NormalizeAvatarId(avatarUuid);
        EnsureOnline();

        var timeout = TimeSpan.FromSeconds(config.Api.GroupOperationTimeoutSeconds);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var requestedAt = DateTimeOffset.UtcNow;
        await _client.Groups.RequestBanAction(
            groupId,
            action,
            [avatarId],
            (_, _) => logger.LogInformation(
                "Group {Operation} action acknowledged for avatar {AvatarId} in group {GroupId}",
                operation,
                avatarId,
                groupId),
            timeoutCts.Token);

        return new GroupOperationResultDto(
            groupId.ToString(),
            avatarId.ToString(),
            operation,
            true,
            requestedAt,
            DateTimeOffset.UtcNow);
    }

    private async Task<IReadOnlyDictionary<UUID, GroupRole>> GetGroupRolesByIdAsync(
        UUID groupId,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(config.Api.GroupOperationTimeoutSeconds);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var tcs = new TaskCompletionSource<GroupRolesDataReplyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestId = UUID.Zero;

        EventHandler<GroupRolesDataReplyEventArgs>? handler = null;
        handler = (_, e) =>
        {
            if (e.RequestID == requestId)
            {
                tcs.TrySetResult(e);
            }
        };

        try
        {
            _client.Groups.GroupRoleDataReply += handler;
            requestId = _client.Groups.RequestGroupRoles(groupId);

            await using var _ = timeoutCts.Token.Register(() =>
                tcs.TrySetCanceled(timeoutCts.Token));

            var reply = await tcs.Task.ConfigureAwait(false);
            return reply.Roles;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out waiting for Second Life group roles after {config.Api.GroupOperationTimeoutSeconds} seconds.");
        }
        finally
        {
            _client.Groups.GroupRoleDataReply -= handler;
        }
    }

    private async Task<IReadOnlyList<KeyValuePair<UUID, UUID>>> GetGroupRoleMembersAsync(
        UUID groupId,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(config.Api.GroupOperationTimeoutSeconds);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var tcs = new TaskCompletionSource<GroupRolesMembersReplyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestId = UUID.Zero;

        EventHandler<GroupRolesMembersReplyEventArgs>? handler = null;
        handler = (_, e) =>
        {
            if (e.RequestID == requestId)
            {
                tcs.TrySetResult(e);
            }
        };

        try
        {
            _client.Groups.GroupRoleMembersReply += handler;
            requestId = _client.Groups.RequestGroupRolesMembers(groupId);

            await using var _ = timeoutCts.Token.Register(() =>
                tcs.TrySetCanceled(timeoutCts.Token));

            var reply = await tcs.Task.ConfigureAwait(false);
            return reply.RolesMembers;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out waiting for Second Life group role members after {config.Api.GroupOperationTimeoutSeconds} seconds.");
        }
        finally
        {
            _client.Groups.GroupRoleMembersReply -= handler;
        }
    }

    public async Task<TeleportResultDto> TeleportAsync(TeleportRequestDto request, CancellationToken cancellationToken)
    {
        if (!_client.Network.Connected)
        {
            throw new InvalidOperationException("Munibot is not logged in.");
        }

        var regionName = TeleportRequestValidator.NormalizeRegionName(request.Region);
        var position = TeleportRequestValidator.NormalizePosition(request.Position);

        await _teleportLock.WaitAsync(cancellationToken);
        try
        {
            var requestedAt = DateTimeOffset.UtcNow;
            logger.LogInformation(
                "Teleporting Munibot to {RegionName} at {Position}",
                regionName,
                position);

            var success = await Task.Run(() => _client.Self.Teleport(regionName, position), cancellationToken);
            if (!success)
            {
                throw new InvalidOperationException($"Teleport to region '{regionName}' failed.");
            }

            return new TeleportResultDto(
                true,
                regionName,
                new Vector3Dto(position.X, position.Y, position.Z),
                _client.Network.CurrentSim?.Name,
                requestedAt,
                DateTimeOffset.UtcNow);
        }
        finally
        {
            _teleportLock.Release();
        }
    }

    public async Task<SendInstantMessageResultDto> SendInstantMessageAsync(
        SendInstantMessageRequestDto request,
        CancellationToken cancellationToken)
    {
        EnsureOnline();

        var avatarId = InstantMessageRequestValidator.NormalizeAvatarId(request.AvatarId);
        var message = InstantMessageRequestValidator.NormalizeMessage(request.Message);
        var requestedAt = DateTimeOffset.UtcNow;

        await Task.Run(() => _client.Self.InstantMessage(avatarId, message), cancellationToken);

        logger.LogInformation(
            "Sent instant message to avatar {AvatarId} length={MessageLength}",
            avatarId,
            message.Length);

        return new SendInstantMessageResultDto(
            avatarId.ToString(),
            true,
            requestedAt,
            DateTimeOffset.UtcNow);
    }

    public async Task<SendLocalChatResultDto> SendLocalChatAsync(
        SendLocalChatRequestDto request,
        CancellationToken cancellationToken)
    {
        EnsureOnline();

        var message = LocalChatRequestValidator.NormalizeMessage(request.Message);
        var channel = LocalChatRequestValidator.NormalizeChannel(request.Channel);
        var chatType = LocalChatRequestValidator.NormalizeChatType(request.ChatType);
        var requestedAt = DateTimeOffset.UtcNow;

        await Task.Run(() => _client.Self.Chat(message, channel, chatType), cancellationToken);

        logger.LogInformation(
            "Sent local chat length={MessageLength} channel={Channel} chatType={ChatType}",
            message.Length,
            channel,
            chatType);

        return new SendLocalChatResultDto(
            true,
            channel,
            chatType.ToString(),
            requestedAt,
            DateTimeOffset.UtcNow);
    }

    public async Task<InventoryItemDto> GetInventoryItemByIdAsync(
        string itemUuid,
        CancellationToken cancellationToken)
    {
        EnsureOnline();

        var itemId = InventoryRequestValidator.NormalizeItemId(itemUuid)
            ?? throw new ArgumentException("A valid Second Life inventory item UUID is required.");

        await _inventoryLock.WaitAsync(cancellationToken);
        try
        {
            var timeout = TimeSpan.FromSeconds(config.Api.InventoryOperationTimeoutSeconds);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            var item = await ResolveInventoryItemAsync(
                itemId,
                itemPath: null,
                fallbackItemName: null,
                fallbackAssetType: null,
                timeout,
                timeoutCts.Token);

            return ToInventoryItemDto(item);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out waiting for Second Life inventory item lookup after {config.Api.InventoryOperationTimeoutSeconds} seconds.");
        }
        finally
        {
            _inventoryLock.Release();
        }
    }

    public async Task<InventoryItemDto> GetInventoryItemByPathAsync(
        string itemPath,
        CancellationToken cancellationToken)
    {
        EnsureOnline();

        var normalizedPath = InventoryRequestValidator.NormalizeItemPath(itemPath)
            ?? throw new ArgumentException("Second Life inventory item path is required.");

        await _inventoryLock.WaitAsync(cancellationToken);
        try
        {
            var timeout = TimeSpan.FromSeconds(config.Api.InventoryOperationTimeoutSeconds);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            var item = await ResolveInventoryItemAsync(
                itemId: null,
                normalizedPath,
                fallbackItemName: null,
                fallbackAssetType: null,
                timeout,
                timeoutCts.Token);

            return ToInventoryItemDto(item);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out waiting for Second Life inventory item lookup after {config.Api.InventoryOperationTimeoutSeconds} seconds.");
        }
        finally
        {
            _inventoryLock.Release();
        }
    }

    public async Task<InventoryGiveResultDto> GiveInventoryItemAsync(
        InventoryGiveRequestDto request,
        CancellationToken cancellationToken)
    {
        EnsureOnline();

        var avatarId = InventoryRequestValidator.NormalizeAvatarId(request.AvatarId);
        var itemId = InventoryRequestValidator.NormalizeItemId(request.ItemId);
        var itemPath = InventoryRequestValidator.NormalizeItemPath(request.ItemPath);
        var fallbackItemName = InventoryRequestValidator.NormalizeItemName(request.ItemName);
        var fallbackAssetType = InventoryRequestValidator.NormalizeAssetType(request.AssetType);
        var doEffect = InventoryRequestValidator.NormalizeDoEffect(request.DoEffect);

        if (!itemId.HasValue && itemPath is null)
        {
            throw new ArgumentException("Either itemId or itemPath is required.");
        }

        await _inventoryLock.WaitAsync(cancellationToken);
        try
        {
            var timeout = TimeSpan.FromSeconds(config.Api.InventoryOperationTimeoutSeconds);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            var requestedAt = DateTimeOffset.UtcNow;
            var item = await ResolveInventoryItemAsync(
                itemId,
                itemPath,
                fallbackItemName,
                fallbackAssetType,
                timeout,
                timeoutCts.Token);

            await Task.Run(
                () => _client.Inventory.GiveItem(item.UUID, item.Name, item.AssetType, avatarId, doEffect),
                timeoutCts.Token);

            logger.LogInformation(
                "Issued inventory give item={ItemId} assetType={AssetType} recipient={AvatarId}",
                item.UUID,
                item.AssetType,
                avatarId);

            return new InventoryGiveResultDto(
                avatarId.ToString(),
                item.UUID.ToString(),
                item.Name,
                item.AssetType.ToString(),
                true,
                doEffect,
                requestedAt,
                DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out waiting for Second Life inventory give after {config.Api.InventoryOperationTimeoutSeconds} seconds.");
        }
        finally
        {
            _inventoryLock.Release();
        }
    }

    public async Task<TextureUploadResultDto> UploadTextureAsync(
        TextureUploadRequestDto request,
        CancellationToken cancellationToken)
    {
        EnsureOnline();

        var name = TextureUploadRequestValidator.NormalizeName(request.Name);
        var description = TextureUploadRequestValidator.NormalizeDescription(request.Description);
        var data = TextureUploadRequestValidator.DecodeTextureData(request.TextureDataBase64);
        TextureUploadRequestValidator.RequireUploadFeeConfirmation(request.ConfirmUploadFee);

        await _inventoryLock.WaitAsync(cancellationToken);
        try
        {
            var timeout = TimeSpan.FromSeconds(config.Api.TextureUploadTimeoutSeconds);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            var requestedAt = DateTimeOffset.UtcNow;
            var folderId = _client.Inventory.FindFolderForType(AssetType.Texture);
            if (folderId == UUID.Zero)
            {
                throw new InvalidOperationException("Second Life did not expose a valid texture inventory folder.");
            }

            var result = await _client.Inventory.CreateItemFromAssetAsync(
                data,
                name,
                description,
                AssetType.Texture,
                InventoryType.Texture,
                folderId,
                Permissions.FullPermissions,
                timeoutCts.Token,
                progress: null);

            if (!result.Success)
            {
                var status = string.IsNullOrWhiteSpace(result.Status)
                    ? result.Error?.Message ?? "unknown error"
                    : result.Status;

                throw result.Error is null
                    ? new InvalidOperationException($"Second Life texture upload failed: {status}")
                    : new InvalidOperationException($"Second Life texture upload failed: {status}", result.Error);
            }

            logger.LogInformation(
                "Uploaded texture {TextureName} item={ItemId} asset={AssetId} bytes={ByteCount}",
                name,
                result.ItemID,
                result.AssetID,
                data.Length);

            return new TextureUploadResultDto(
                result.ItemID.ToString(),
                result.AssetID.ToString(),
                name,
                true,
                result.Status,
                data.Length,
                TextureUploadRequestValidator.ExpectedTextureUploadCostLinden,
                requestedAt,
                DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out waiting for Second Life texture upload after {config.Api.TextureUploadTimeoutSeconds} seconds.");
        }
        finally
        {
            _inventoryLock.Release();
        }
    }

    public async Task<WalletBalanceDto> GetWalletBalanceAsync(CancellationToken cancellationToken)
    {
        EnsureOnline();

        await _walletLock.WaitAsync(cancellationToken);
        try
        {
            var timeout = TimeSpan.FromSeconds(config.Api.WalletOperationTimeoutSeconds);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            var requestedAt = DateTimeOffset.UtcNow;
            var tcs = new TaskCompletionSource<BalanceEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

            EventHandler<BalanceEventArgs>? handler = null;
            handler = (_, e) => tcs.TrySetResult(e);

            try
            {
                _client.Self.MoneyBalance += handler;

                await Task.Run(() => _client.Self.RequestBalance(), timeoutCts.Token);

                await using var _ = timeoutCts.Token.Register(() =>
                    tcs.TrySetCanceled(timeoutCts.Token));

                var reply = await tcs.Task.ConfigureAwait(false);

                logger.LogInformation(
                    "Fetched wallet balance for agent {AgentId}: balance={Balance}",
                    _client.Self.AgentID,
                    reply.Balance);

                return new WalletBalanceDto(
                    reply.Balance,
                    _client.Self.AgentID.ToString(),
                    requestedAt,
                    DateTimeOffset.UtcNow);
            }
            finally
            {
                _client.Self.MoneyBalance -= handler;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out waiting for Second Life wallet balance after {config.Api.WalletOperationTimeoutSeconds} seconds.");
        }
        finally
        {
            _walletLock.Release();
        }
    }

    public async Task<WalletPayResultDto> PayAvatarAsync(
        WalletPayRequestDto request,
        CancellationToken cancellationToken)
    {
        EnsureOnline();

        var avatarId = WalletRequestValidator.NormalizeAvatarId(request.AvatarId);
        var amount = WalletRequestValidator.NormalizeAmount(request.Amount);
        var description = WalletRequestValidator.NormalizeDescription(request.Description);
        WalletRequestValidator.RequirePaymentConfirmation(request.ConfirmPayment);

        await _walletLock.WaitAsync(cancellationToken);
        try
        {
            var timeout = TimeSpan.FromSeconds(config.Api.WalletOperationTimeoutSeconds);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            var requestedAt = DateTimeOffset.UtcNow;
            var tcs = new TaskCompletionSource<MoneyBalanceReplyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

            EventHandler<MoneyBalanceReplyEventArgs>? handler = null;
            handler = (_, e) => tcs.TrySetResult(e);

            try
            {
                _client.Self.MoneyBalanceReply += handler;

                await Task.Run(
                    () => _client.Self.GiveMoney(
                        avatarId,
                        amount,
                        description,
                        MoneyTransactionType.Gift,
                        TransactionFlags.None),
                    timeoutCts.Token);

                await using var walletPaymentReplyTimeoutRegistration = timeoutCts.Token.Register(() =>
                    tcs.TrySetCanceled(timeoutCts.Token));

                var reply = await tcs.Task.ConfigureAwait(false);
                if (!reply.Success)
                {
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(reply.Description)
                            ? "Second Life rejected the outgoing payment."
                            : $"Second Life rejected the outgoing payment: {reply.Description}");
                }

                logger.LogInformation(
                    "Issued wallet payment to avatar {AvatarId}: amount={Amount} transaction={TransactionId} descriptionLength={DescriptionLength}",
                    avatarId,
                    amount,
                    reply.TransactionID,
                    description.Length);

                _ = PublishWalletEventAsync(WalletEventMapper.FromOutgoingPaymentResult(
                    reply,
                    _client.Self.AgentID,
                    avatarId,
                    amount,
                    description,
                    DateTimeOffset.UtcNow));

                return new WalletPayResultDto(
                    avatarId.ToString(),
                    amount,
                    true,
                    reply.TransactionID == UUID.Zero ? null : reply.TransactionID.ToString(),
                    reply.Balance,
                    string.IsNullOrWhiteSpace(reply.Description) ? null : reply.Description,
                    requestedAt,
                    DateTimeOffset.UtcNow);
            }
            finally
            {
                _client.Self.MoneyBalanceReply -= handler;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out waiting for Second Life outgoing payment after {config.Api.WalletOperationTimeoutSeconds} seconds.");
        }
        finally
        {
            _walletLock.Release();
        }
    }

    public async Task<AvatarKeyResolutionResponseDto> ResolveAvatarKeysAsync(
        AvatarKeyResolutionRequestDto request,
        CancellationToken cancellationToken)
    {
        if (!_client.Network.Connected)
        {
            throw new InvalidOperationException("Munibot is not logged in.");
        }

        var avatarIds = AvatarRequestValidator.NormalizeAvatarIds(request.AvatarIds);
        await _avatarLookupLock.WaitAsync(cancellationToken);
        try
        {
            var timeout = TimeSpan.FromSeconds(config.Api.AvatarLookupTimeoutSeconds);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            var requestedAt = DateTimeOffset.UtcNow;
            var remaining = avatarIds.ToHashSet();
            var names = new Dictionary<UUID, string>();
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            EventHandler<UUIDNameReplyEventArgs>? handler = null;
            handler = (_, e) =>
            {
                foreach (var (id, name) in e.Names)
                {
                    if (!remaining.Contains(id))
                    {
                        continue;
                    }

                    names[id] = name;
                    remaining.Remove(id);
                }

                if (remaining.Count == 0)
                {
                    tcs.TrySetResult();
                }
            };

            try
            {
                _client.Avatars.UUIDNameReply += handler;
                _client.Avatars.RequestAvatarNames(avatarIds.ToList());

                await using var _ = timeoutCts.Token.Register(() =>
                    tcs.TrySetCanceled(timeoutCts.Token));

                await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                _client.Avatars.UUIDNameReply -= handler;
            }

            var results = avatarIds
                .Select(id => new AvatarKeyResolutionDto(
                    id.ToString(),
                    names.TryGetValue(id, out var name) ? name : null))
                .ToList();

            return new AvatarKeyResolutionResponseDto(results, requestedAt, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out waiting for Second Life avatar name lookup after {config.Api.AvatarLookupTimeoutSeconds} seconds.");
        }
        finally
        {
            _avatarLookupLock.Release();
        }
    }

    public async Task<AvatarNameResolutionResponseDto> ResolveAvatarNamesAsync(
        AvatarNameResolutionRequestDto request,
        CancellationToken cancellationToken)
    {
        if (!_client.Network.Connected)
        {
            throw new InvalidOperationException("Munibot is not logged in.");
        }

        var names = AvatarRequestValidator.NormalizeNames(request.Names);
        var requestedAt = DateTimeOffset.UtcNow;
        var results = new List<AvatarNameResolutionDto>();

        await _avatarLookupLock.WaitAsync(cancellationToken);
        try
        {
            foreach (var name in names)
            {
                var candidates = await SearchAvatarPickerAsync(name, cancellationToken);
                var exact = candidates.FirstOrDefault(candidate =>
                    string.Equals(candidate.AvatarName, name, StringComparison.OrdinalIgnoreCase));
                exact ??= candidates.FirstOrDefault();

                results.Add(new AvatarNameResolutionDto(
                    name,
                    exact?.AvatarId,
                    exact?.AvatarName,
                    candidates));
            }

            return new AvatarNameResolutionResponseDto(results, requestedAt, DateTimeOffset.UtcNow);
        }
        finally
        {
            _avatarLookupLock.Release();
        }
    }

    public async Task<AvatarSearchResponseDto> SearchPeopleAsync(string searchText, CancellationToken cancellationToken)
    {
        if (!_client.Network.Connected)
        {
            throw new InvalidOperationException("Munibot is not logged in.");
        }

        var query = AvatarRequestValidator.NormalizeSearchText(searchText);
        await _peopleSearchLock.WaitAsync(cancellationToken);
        try
        {
            var timeout = TimeSpan.FromSeconds(config.Api.AvatarLookupTimeoutSeconds);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            var requestedAt = DateTimeOffset.UtcNow;
            var tcs = new TaskCompletionSource<DirPeopleReplyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var queryId = UUID.Zero;

            EventHandler<DirPeopleReplyEventArgs>? handler = null;
            handler = (_, e) =>
            {
                if (e.QueryID == queryId)
                {
                    tcs.TrySetResult(e);
                }
            };

            try
            {
                _client.Directory.DirPeopleReply += handler;
                queryId = _client.Directory.StartPeopleSearch(query, 0);

                await using var _ = timeoutCts.Token.Register(() =>
                    tcs.TrySetCanceled(timeoutCts.Token));

                var reply = await tcs.Task.ConfigureAwait(false);
                var candidates = reply.MatchedPeople
                    .Select(person => new AvatarSearchCandidateDto(
                        person.AgentID.ToString(),
                        $"{person.FirstName} {person.LastName}".Trim(),
                        person.Online))
                    .OrderBy(candidate => candidate.AvatarName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return new AvatarSearchResponseDto(query, candidates, requestedAt, DateTimeOffset.UtcNow);
            }
            finally
            {
                _client.Directory.DirPeopleReply -= handler;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out waiting for Second Life people search after {config.Api.AvatarLookupTimeoutSeconds} seconds.");
        }
        finally
        {
            _peopleSearchLock.Release();
        }
    }

    public async Task<ExperiencePreferencesDto> GetExperiencePreferencesAsync(CancellationToken cancellationToken)
    {
        if (!_client.Network.Connected)
        {
            throw new InvalidOperationException("Munibot is not logged in.");
        }

        var preferences = await GetExperiencePreferencesCoreAsync(cancellationToken);
        return ToExperiencePreferencesDto(preferences, DateTimeOffset.UtcNow);
    }

    public async Task<ExperienceOperationResultDto> AllowExperienceAsync(
        string experienceUuid,
        CancellationToken cancellationToken)
    {
        if (!_client.Network.Connected)
        {
            throw new InvalidOperationException("Munibot is not logged in.");
        }

        var experienceId = ExperienceRequestValidator.NormalizeExperienceId(experienceUuid);
        return await AllowExperienceCoreAsync(experienceId, cancellationToken);
    }

    private async Task AllowConfiguredExperiencesAsync(CancellationToken cancellationToken)
    {
        if (config.Experiences.AutoAllow.Count == 0)
        {
            return;
        }

        foreach (var experience in config.Experiences.AutoAllow)
        {
            var experienceId = ExperienceRequestValidator.NormalizeExperienceId(experience.Id);

            try
            {
                var result = await AllowExperienceCoreAsync(experienceId, cancellationToken);
                logger.LogInformation(
                    "Configured experience {ExperienceId} ({ExperienceName}) is allowed; changed={Changed}",
                    result.ExperienceId,
                    experience.Name ?? "unnamed",
                    result.Changed);
            }
            catch (InvalidOperationException ex)
                when (ex.Message.Contains("experience preferences are not available", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation(
                    "Configured experience {ExperienceId} ({ExperienceName}) could not be auto-allowed because Second Life did not expose experience preferences in this simulator.",
                    experience.Id,
                    experience.Name ?? "unnamed");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(
                    ex,
                    "Failed to auto-allow configured experience {ExperienceId} ({ExperienceName})",
                    experience.Id,
                    experience.Name ?? "unnamed");
            }
        }
    }

    private async Task<ExperienceOperationResultDto> AllowExperienceCoreAsync(
        UUID experienceId,
        CancellationToken cancellationToken)
    {
        var requestedAt = DateTimeOffset.UtcNow;

        await _experienceLock.WaitAsync(cancellationToken);
        try
        {
            var preferences = await LoadExperiencePreferencesAsync(cancellationToken);
            var changed = false;

            if (preferences.Blocked.RemoveAll(id => id == experienceId) > 0)
            {
                changed = true;
            }

            if (!preferences.Allowed.Contains(experienceId))
            {
                preferences.Allowed.Add(experienceId);
                changed = true;
            }

            if (changed)
            {
                await _client.Self.SetExperiencePreferencesAsync(preferences, cancellationToken);
                preferences = await LoadExperiencePreferencesAsync(cancellationToken);
            }

            if (!preferences.Allowed.Contains(experienceId))
            {
                throw new InvalidOperationException(
                    $"Second Life did not confirm experience {experienceId} in the allowed list.");
            }

            return new ExperienceOperationResultDto(
                experienceId.ToString(),
                "allow",
                changed,
                preferences.Allowed.Select(id => id.ToString()).ToList(),
                preferences.Blocked.Select(id => id.ToString()).ToList(),
                requestedAt,
                DateTimeOffset.UtcNow);
        }
        finally
        {
            _experienceLock.Release();
        }
    }

    private async Task<ExperiencePreferencesMessage> GetExperiencePreferencesCoreAsync(
        CancellationToken cancellationToken)
    {
        await _experienceLock.WaitAsync(cancellationToken);
        try
        {
            return await LoadExperiencePreferencesAsync(cancellationToken);
        }
        finally
        {
            _experienceLock.Release();
        }
    }

    private async Task<ExperiencePreferencesMessage> LoadExperiencePreferencesAsync(
        CancellationToken cancellationToken)
    {
        var preferences = await _client.Self.GetExperiencePreferencesAsync(cancellationToken)
            ?? await _client.Self.GetAgentExperiencePermissionsAsync(cancellationToken);

        return preferences
            ?? throw new InvalidOperationException(
                "Second Life experience preferences are not available in the current simulator.");
    }

    private async Task<IReadOnlyList<AvatarSearchCandidateDto>> SearchAvatarPickerAsync(
        string avatarName,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(config.Api.AvatarLookupTimeoutSeconds);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var queryId = UUID.Random();
        var tcs = new TaskCompletionSource<AvatarPickerReplyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

        EventHandler<AvatarPickerReplyEventArgs>? handler = null;
        handler = (_, e) =>
        {
            if (e.QueryID == queryId)
            {
                tcs.TrySetResult(e);
            }
        };

        try
        {
            _client.Avatars.AvatarPickerReply += handler;
            _client.Avatars.RequestAvatarNameSearch(avatarName, queryId);

            await using var _ = timeoutCts.Token.Register(() =>
                tcs.TrySetCanceled(timeoutCts.Token));

            var reply = await tcs.Task.ConfigureAwait(false);
            return reply.Avatars
                .Select(avatar => new AvatarSearchCandidateDto(
                    avatar.Key.ToString(),
                    avatar.Value))
                .OrderBy(candidate => candidate.AvatarName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out waiting for Second Life avatar name lookup after {config.Api.AvatarLookupTimeoutSeconds} seconds.");
        }
        finally
        {
            _client.Avatars.AvatarPickerReply -= handler;
        }
    }

    private async Task<InventoryItem> ResolveInventoryItemAsync(
        UUID? itemId,
        string? itemPath,
        string? fallbackItemName,
        AssetType? fallbackAssetType,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var resolvedItemId = itemId;
        if (!resolvedItemId.HasValue && itemPath is not null)
        {
            var store = _client.Inventory.Store
                ?? throw new InvalidOperationException("Second Life inventory store is not available.");
            var rootFolderId = store.RootFolder?.UUID ?? UUID.Zero;
            if (rootFolderId == UUID.Zero)
            {
                throw new InvalidOperationException("Second Life inventory root folder is not available.");
            }

            resolvedItemId = await Task.Run(
                () => _client.Inventory.FindObjectByPath(rootFolderId, _client.Self.AgentID, itemPath, timeout),
                cancellationToken);
        }

        if (!resolvedItemId.HasValue || resolvedItemId.Value == UUID.Zero)
        {
            throw new KeyNotFoundException($"Second Life inventory item '{itemPath}' was not found.");
        }

        var inventoryStore = _client.Inventory.Store
            ?? throw new InvalidOperationException("Second Life inventory store is not available.");

        if (inventoryStore.TryGetValue<InventoryItem>(resolvedItemId.Value, out var localItem) &&
            localItem is not null)
        {
            return localItem;
        }

        var fetchedItem = await _client.Inventory.FetchItemAsync(
            resolvedItemId.Value,
            _client.Self.AgentID,
            cancellationToken);

        if (fetchedItem is not null)
        {
            return fetchedItem;
        }

        if (fallbackItemName is not null && fallbackAssetType.HasValue)
        {
            return new InventoryItem(resolvedItemId.Value)
            {
                Name = fallbackItemName,
                OwnerID = _client.Self.AgentID,
                AssetType = fallbackAssetType.Value
            };
        }

        throw new KeyNotFoundException(
            $"Second Life inventory item '{resolvedItemId.Value}' was not found in Munibot's inventory cache.");
    }

    private static InventoryItemDto ToInventoryItemDto(InventoryItem item)
        => new(
            item.UUID.ToString(),
            item.AssetUUID == UUID.Zero ? null : item.AssetUUID.ToString(),
            item.Name,
            item.AssetType.ToString(),
            item.InventoryType.ToString(),
            item.ParentUUID.ToString(),
            item.OwnerID.ToString(),
            string.IsNullOrWhiteSpace(item.Description) ? null : item.Description);

    private static GroupMemberDto ToMemberDto(UUID id, GroupMember member)
        => new(
            id.ToString(),
            member.Title,
            member.OnlineStatus,
            member.IsOwner,
            member.Contribution,
            member.Powers.ToString());

    private void EnsureOnline()
    {
        if (!_client.Network.Connected)
        {
            throw new InvalidOperationException("Munibot is not logged in.");
        }
    }

    private void WireEvents()
    {
        if (_eventsWired)
        {
            return;
        }

        _client.Network.LoginProgress += (_, e) =>
        {
            if (e.Status == LoginStatus.Failed)
            {
                logger.LogWarning("Login progress: {Status} - {Message} ({Reason})", e.Status, e.Message, e.FailReason);
                return;
            }

            logger.LogInformation("Login progress: {Status} - {Message}", e.Status, e.Message);
        };

        _client.Network.Disconnected += (_, e) =>
        {
            LastDisconnectReason = $"{e.Reason}: {e.Message}";
            logger.LogWarning("Disconnected: {Reason} - {Message}", e.Reason, e.Message);
        };

        _client.Self.MoneyBalance += (_, e) => _ = HandleWalletBalanceAsync(e);
        _client.Self.MoneyBalanceReply += (_, e) => _ = HandleWalletBalanceReplyAsync(e);

        if (config.Diagnostics.LogSecondLifeEvents)
        {
            _client.Network.RegisterCallback(PacketType.GenericMessage, (_, e) => LogGenericMessage(e));
            _client.Self.ChatFromSimulator += (_, e) => LogSecondLifeEvent("chat", e);
            _client.Self.IM += (_, e) => LogSecondLifeEvent("instant-message", e);
            _client.Self.TeleportProgress += (_, e) => LogSecondLifeEvent("teleport-progress", e);
            _client.Self.AlertMessage += (_, e) => LogSecondLifeEvent("alert-message", e);
            _client.Self.ScriptDialog += (_, e) => LogSecondLifeEvent("script-dialog", e);
            _client.Inventory.InventoryObjectOffered += (_, e) => LogSecondLifeEvent("inventory-offer", e);
        }

        _eventsWired = true;
    }

    private void LogSecondLifeEvent(string eventName, object eventArgs)
    {
        var values = SecondLifeEventFormatter.Format(eventArgs, config.Diagnostics.MaxLoggedBodyBytes);
        logger.LogInformation("SL event {EventName}: {@EventValues}", eventName, values);
    }

    private async Task HandleWalletBalanceAsync(BalanceEventArgs eventArgs)
    {
        try
        {
            if (config.Diagnostics.LogSecondLifeEvents)
            {
                LogSecondLifeEvent("money-balance", eventArgs);
            }

            var observedAtUtc = DateTimeOffset.UtcNow;
            var (previousBalance, delta) = ObserveWalletBalance(eventArgs.Balance);

            logger.LogInformation(
                "Wallet balance update observed balance={Balance} previous={PreviousBalance} delta={Delta}",
                eventArgs.Balance,
                previousBalance,
                delta);

            if (previousBalance.HasValue && delta is > 0)
            {
                await ReconcileWalletBalanceIncreaseAsync(
                    previousBalance.Value,
                    eventArgs.Balance,
                    delta.Value,
                    observedAtUtc);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Wallet balance update handling failed unexpectedly.");
        }
    }

    private async Task HandleWalletBalanceReplyAsync(MoneyBalanceReplyEventArgs eventArgs)
    {
        try
        {
            if (config.Diagnostics.LogSecondLifeEvents)
            {
                LogSecondLifeEvent("money-balance-reply", eventArgs);
            }

            var observedAtUtc = DateTimeOffset.UtcNow;
            var (previousBalance, delta) = ObserveWalletBalance(eventArgs.Balance);

            logger.LogInformation(
                "Wallet balance reply observed transaction={TransactionId} balance={Balance} previous={PreviousBalance} delta={Delta}",
                eventArgs.TransactionID,
                eventArgs.Balance,
                previousBalance,
                delta);

            if (previousBalance.HasValue && delta is > 0)
            {
                await ReconcileWalletBalanceIncreaseAsync(
                    previousBalance.Value,
                    eventArgs.Balance,
                    delta.Value,
                    observedAtUtc);
            }

            await PublishWalletEventAsync(eventArgs);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Wallet balance reply handling failed unexpectedly transaction={TransactionId}",
                eventArgs.TransactionID);
        }
    }

    private (int? PreviousBalance, int? Delta) ObserveWalletBalance(int balance)
    {
        lock (_walletBalanceStateLock)
        {
            var previous = _lastObservedWalletBalance;
            _lastObservedWalletBalance = balance;
            return (previous, previous.HasValue ? balance - previous.Value : null);
        }
    }

    private async Task ReconcileWalletBalanceIncreaseAsync(
        int previousBalance,
        int currentBalance,
        int observedDelta,
        DateTimeOffset observedAtUtc)
    {
        if (!config.Munibase.WalletEvents.IsConfigured)
        {
            logger.LogWarning(
                "Wallet balance increased by L${ObservedDelta}, but Munibase wallet event delivery is not configured; skipping treasury callback.",
                observedDelta);
            return;
        }

        logger.LogInformation(
            "Wallet balance increased by L${ObservedDelta}; reconciling recent account history for callback callback.",
            observedDelta);

        if (!await _walletHistoryReconcileLock.WaitAsync(0))
        {
            logger.LogInformation(
                "Wallet history reconciliation is already running; skipping overlapping balance increase previous={PreviousBalance} current={CurrentBalance} delta={ObservedDelta}.",
                previousBalance,
                currentBalance,
                observedDelta);
            return;
        }

        try
        {
            var attempts = Math.Max(config.Munibase.WalletEvents.HistoryReconcileAttempts, 1);
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                if (attempt > 1 && config.Munibase.WalletEvents.HistoryReconcileDelaySeconds > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(config.Munibase.WalletEvents.HistoryReconcileDelaySeconds));
                }

                var published = await PublishIncomingWalletEventsFromHistoryAsync(
                    currentBalance,
                    observedDelta,
                    observedAtUtc);

                if (published > 0)
                {
                    logger.LogInformation(
                        "Wallet balance increase reconciled via account history previous={PreviousBalance} current={CurrentBalance} delta={ObservedDelta} published={PublishedCount} attempt={Attempt}.",
                        previousBalance,
                        currentBalance,
                        observedDelta,
                        published,
                        attempt);
                    return;
                }
            }

            logger.LogWarning(
                "Wallet balance increased by L${ObservedDelta} previous={PreviousBalance} current={CurrentBalance}, but no matching positive account-history transaction was found.",
                observedDelta,
                previousBalance,
                currentBalance);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Wallet balance increase reconciliation failed previous={PreviousBalance} current={CurrentBalance} delta={ObservedDelta}.",
                previousBalance,
                currentBalance,
                observedDelta);
        }
        finally
        {
            _walletHistoryReconcileLock.Release();
        }
    }

    private async Task<int> PublishIncomingWalletEventsFromHistoryAsync(
        int currentBalance,
        int observedDelta,
        DateTimeOffset observedAtUtc)
    {
        var lookback = TimeSpan.FromMinutes(config.Munibase.WalletEvents.HistoryLookbackMinutes);
        var fromUtc = observedAtUtc.Subtract(lookback);
        var toUtc = observedAtUtc.AddMinutes(1);

        var history = await accountHistoryClient.GetTransactionsAsync(fromUtc, toUtc);
        var candidates = history.Transactions
            .Where(transaction => transaction.OccurredAtUtc >= fromUtc && transaction.OccurredAtUtc <= toUtc)
            .Where(transaction => IsPotentialIncomingWalletTransaction(transaction, currentBalance))
            .OrderBy(transaction => transaction.OccurredAtUtc)
            .ThenBy(transaction => transaction.TransactionId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var published = 0;
        foreach (var transaction in candidates)
        {
            if (HasDeliveredWalletTransaction(transaction.TransactionId))
            {
                continue;
            }

            var sourceAvatarId = await ResolveAccountHistoryResidentAsync(transaction.Resident);
            if (string.IsNullOrWhiteSpace(sourceAvatarId))
            {
                logger.LogWarning(
                    "Skipping account-history wallet transaction {TransactionId}; resident '{Resident}' could not be resolved to an avatar UUID.",
                    transaction.TransactionId,
                    transaction.Resident);
                continue;
            }

            var walletEvent = WalletEventMapper.FromAccountHistoryTransaction(
                transaction,
                sourceAvatarId,
                _client.Self.AgentID,
                observedDelta);

            if (walletEvent is null)
            {
                logger.LogDebug(
                    "Skipping account-history wallet transaction {TransactionId}; transaction did not contain enough details.",
                    transaction.TransactionId);
                continue;
            }

            await PublishWalletEventAsync(walletEvent);
            published++;
        }

        return published;
    }

    private static bool IsPotentialIncomingWalletTransaction(
        AccountHistoryTransactionDto transaction,
        int currentBalance)
    {
        if (string.IsNullOrWhiteSpace(transaction.Resident))
        {
            return false;
        }

        if (transaction.InferredAmountDelta.HasValue)
        {
            return transaction.InferredAmountDelta.Value > 0;
        }

        return transaction.EndBalance == unchecked((uint)currentBalance);
    }

    private async Task<string?> ResolveAccountHistoryResidentAsync(string? resident)
    {
        var residentName = NormalizeAccountHistoryResidentName(resident);
        if (residentName is null)
        {
            return null;
        }

        try
        {
            var response = await ResolveAvatarNamesAsync(new AvatarNameResolutionRequestDto([residentName]), CancellationToken.None);
            return response.Results
                .FirstOrDefault(result => string.Equals(result.AvatarName, residentName, StringComparison.OrdinalIgnoreCase))
                ?.AvatarId
                ?? response.Results.FirstOrDefault()?.AvatarId;
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            logger.LogWarning(
                ex,
                "Unable to resolve account-history resident '{Resident}' to an avatar UUID.",
                residentName);
            return null;
        }
    }

    private static string? NormalizeAccountHistoryResidentName(string? resident)
    {
        var name = resident?.Replace('.', ' ').Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private async Task PublishWalletEventAsync(MoneyBalanceReplyEventArgs eventArgs)
    {
        try
        {
            var walletEvent = WalletEventMapper.FromMoneyBalanceReply(eventArgs, _client.Self.AgentID);
            if (walletEvent is null)
            {
                logger.LogDebug(
                    "Skipping wallet event delivery for transaction {TransactionId}; event did not contain enough transaction details.",
                    eventArgs.TransactionID);
                return;
            }

            await PublishWalletEventAsync(walletEvent);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Wallet event delivery failed unexpectedly transaction={TransactionId}",
                eventArgs.TransactionID);
        }
    }

    private async Task PublishWalletEventAsync(WalletEventDto walletEvent)
    {
        try
        {
            if (HasDeliveredWalletTransaction(walletEvent.TransactionId))
            {
                logger.LogDebug(
                    "Skipping duplicate wallet event delivery transaction={TransactionId}",
                    walletEvent.TransactionId);
                return;
            }

            var result = await walletEventPublisher.PublishAsync(walletEvent);
            if (result.Delivered)
            {
                RememberDeliveredWalletTransaction(walletEvent.TransactionId);
            }

            if (result.Enabled && !result.Delivered)
            {
                logger.LogWarning(
                    "Wallet event delivery did not complete transaction={TransactionId} attempts={Attempts} status={Status}",
                    walletEvent.TransactionId,
                    result.Attempts,
                    result.Status);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Wallet event delivery failed unexpectedly transaction={TransactionId}",
                walletEvent.TransactionId);
        }
    }

    private bool HasDeliveredWalletTransaction(string? transactionId)
    {
        if (string.IsNullOrWhiteSpace(transactionId))
        {
            return false;
        }

        lock (_walletEventTransactionLock)
        {
            return _deliveredWalletEventTransactionIds.Contains(transactionId.Trim());
        }
    }

    private void RememberDeliveredWalletTransaction(string? transactionId)
    {
        if (string.IsNullOrWhiteSpace(transactionId))
        {
            return;
        }

        lock (_walletEventTransactionLock)
        {
            _deliveredWalletEventTransactionIds.Add(transactionId.Trim());
        }
    }

    private void LogGenericMessage(PacketReceivedEventArgs eventArgs)
    {
        if (eventArgs.Packet is not GenericMessagePacket message)
        {
            return;
        }

        var method = Utils.BytesToString(message.MethodData.Method);
        if (!string.Equals(method, "ExperienceEvent", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug("SL generic message {Method}", method);
            return;
        }

        var parameters = message.ParamList
            .Select((parameter, index) => new GenericMessageParameterDto(
                index,
                FormatGenericMessageParameter(parameter.Parameter)))
            .ToList();

        logger.LogInformation(
            "SL event generic-message: method={Method} invoice={Invoice} transactionId={TransactionId} params={@Parameters}",
            method,
            message.MethodData.Invoice,
            message.AgentData.TransactionID,
            parameters);
    }

    private string FormatGenericMessageParameter(byte[] parameter)
    {
        var value = Utils.BytesToString(parameter);
        if (value.Length > 0 && value.Length <= config.Diagnostics.MaxLoggedBodyBytes)
        {
            return value;
        }

        var maxBytes = Math.Min(parameter.Length, Math.Max(config.Diagnostics.MaxLoggedBodyBytes, 0));
        return $"hex:{Convert.ToHexString(parameter.AsSpan(0, maxBytes))}";
    }

    private static ExperiencePreferencesDto ToExperiencePreferencesDto(
        ExperiencePreferencesMessage preferences,
        DateTimeOffset retrievedAt)
        => new(
            preferences.Allowed.Select(id => id.ToString()).ToList(),
            preferences.Blocked.Select(id => id.ToString()).ToList(),
            retrievedAt);

    private sealed record GenericMessageParameterDto(int Index, string Value);

    public ValueTask DisposeAsync()
    {
        Logout();
        _client.Dispose();
        _groupRosterLock.Dispose();
        _groupOperationLock.Dispose();
        _teleportLock.Dispose();
        _avatarLookupLock.Dispose();
        _peopleSearchLock.Dispose();
        _experienceLock.Dispose();
        _inventoryLock.Dispose();
        _walletLock.Dispose();
        return ValueTask.CompletedTask;
    }
}

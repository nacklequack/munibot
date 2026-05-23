using Microsoft.Extensions.Logging;
using OpenMetaverse;

namespace Munibot;

public sealed class SecondLifeBotSession(BotConfig config, ILogger<SecondLifeBotSession> logger) : IAsyncDisposable
{
    private readonly GridClient _client = new();
    private readonly SemaphoreSlim _groupRosterLock = new(1, 1);
    private readonly SemaphoreSlim _groupOperationLock = new(1, 1);
    private readonly SemaphoreSlim _teleportLock = new(1, 1);
    private readonly SemaphoreSlim _avatarLookupLock = new(1, 1);
    private readonly SemaphoreSlim _peopleSearchLock = new(1, 1);
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

        if (config.Diagnostics.LogSecondLifeEvents)
        {
            _client.Self.ChatFromSimulator += (_, e) => LogSecondLifeEvent("chat", e);
            _client.Self.IM += (_, e) => LogSecondLifeEvent("instant-message", e);
            _client.Self.MoneyBalance += (_, e) => LogSecondLifeEvent("money-balance", e);
            _client.Self.MoneyBalanceReply += (_, e) => LogSecondLifeEvent("money-balance-reply", e);
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

    public ValueTask DisposeAsync()
    {
        Logout();
        _client.Dispose();
        _groupRosterLock.Dispose();
        _groupOperationLock.Dispose();
        _teleportLock.Dispose();
        _avatarLookupLock.Dispose();
        _peopleSearchLock.Dispose();
        return ValueTask.CompletedTask;
    }
}

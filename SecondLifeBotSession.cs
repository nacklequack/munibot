using Microsoft.Extensions.Logging;
using OpenMetaverse;

namespace Munibot;

public sealed class SecondLifeBotSession(BotConfig config, ILogger<SecondLifeBotSession> logger) : IAsyncDisposable
{
    private readonly GridClient _client = new();
    private readonly SemaphoreSlim _groupRosterLock = new(1, 1);
    private bool _eventsWired;

    public bool IsOnline => _client.Network.Connected;
    public string? CurrentSimulator => _client.Network.CurrentSim?.Name;
    public string AgentId => _client.Self.AgentID.ToString();
    public string? LastDisconnectReason { get; private set; }

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

    public async Task<GroupRosterDto> GetGroupRosterAsync(string groupUuid, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(groupUuid, out var groupId) || groupId == UUID.Zero)
        {
            throw new ArgumentException("A valid Second Life group UUID is required.", nameof(groupUuid));
        }

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

    private static GroupMemberDto ToMemberDto(UUID id, GroupMember member)
        => new(
            id.ToString(),
            member.Title,
            member.OnlineStatus,
            member.IsOwner,
            member.Contribution,
            member.Powers.ToString());

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
        return ValueTask.CompletedTask;
    }
}

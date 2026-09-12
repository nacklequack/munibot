using OpenMetaverse;

namespace Munibot;

public sealed class GridActiveGroupTransport(GridClient client) : IActiveGroupTransport
{
    public UUID? SessionId => client.Network.Connected ? client.Self.SessionID : null;

    public Task<BotActiveGroupDto> ReadAsync(CancellationToken ct) =>
        WaitForStateAsync(client.Groups.RequestCurrentGroups, null, ct);

    public Task<BotActiveGroupDto> ActivateAsync(UUID groupId, CancellationToken ct) =>
        WaitForStateAsync(() => client.Groups.ActivateGroup(groupId), groupId, ct);

    private async Task<BotActiveGroupDto> WaitForStateAsync(Action request, UUID? expectedGroup, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<BotActiveGroupDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnAgentData(object? sender, AgentDataReplyEventArgs e)
        {
            if (expectedGroup.HasValue && e.ActiveGroupID != expectedGroup.Value) return;
            completion.TrySetResult(new(e.ActiveGroupID.ToString(), e.GroupName, e.GroupTitle,
                e.GroupPowers.ToString(), DateTimeOffset.UtcNow));
        }

        client.Self.AgentDataReply += OnAgentData;
        try { return await RequestAsync(request, completion, ct); }
        finally { client.Self.AgentDataReply -= OnAgentData; }
    }

    public async Task<bool> IsMemberAsync(UUID groupId, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnGroups(object? sender, CurrentGroupsEventArgs e) => completion.TrySetResult(e.Groups.ContainsKey(groupId));
        client.Groups.CurrentGroups += OnGroups;
        try { return await RequestAsync(client.Groups.RequestCurrentGroups, completion, ct); }
        finally { client.Groups.CurrentGroups -= OnGroups; }
    }

    private async Task<T> RequestAsync<T>(Action request, TaskCompletionSource<T> completion, CancellationToken ct)
    {
        var sessionId = SessionId ?? throw new ActiveGroupException("bot_offline", "Munibot is not logged in.");
        void OnDisconnected(object? sender, DisconnectedEventArgs e) => completion.TrySetException(
            new ActiveGroupException("bot_offline", "The simulator connection was lost during the active-group operation."));
        client.Network.Disconnected += OnDisconnected;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (SessionId != sessionId) throw new ActiveGroupException("bot_offline", "The bot session changed.");
            request();
            var result = await completion.Task.WaitAsync(ct);
            if (SessionId != sessionId) throw new ActiveGroupException("bot_reconnected", "The bot session changed.");
            return result;
        }
        finally { client.Network.Disconnected -= OnDisconnected; }
    }
}

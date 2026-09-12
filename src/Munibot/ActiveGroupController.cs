using OpenMetaverse;

namespace Munibot;

public interface IActiveGroupTransport
{
    // A new login invalidates requests sent on the previous simulator circuit.
    UUID? SessionId { get; }
    Task<BotActiveGroupDto> ReadAsync(CancellationToken ct);
    Task<bool> IsMemberAsync(UUID groupId, CancellationToken ct);
    Task<BotActiveGroupDto> ActivateAsync(UUID groupId, CancellationToken ct);
}

public sealed class ActiveGroupController(
    IActiveGroupTransport transport, SemaphoreSlim teleportLock, SemaphoreSlim inventoryLock,
    TimeSpan timeout) : IActiveGroupService
{
    // Accessed only while holding the session's inventory lock, including during reconciliation.
    private (UUID GroupId, UUID SessionId)? _pending;

    public Task<BotActiveGroupDto> GetActiveGroupAsync(CancellationToken ct) =>
        WithLocksAsync(async token =>
        {
            var state = await ReadAndReconcileAsync(token);
            return state with { PendingGroupId = _pending?.GroupId.ToString() };
        }, ct);

    public Task<SetActiveGroupResultDto> SetActiveGroupAsync(SetActiveGroupRequestDto request, CancellationToken ct)
    {
        var groupId = GroupRequestValidator.NormalizeGroupId(request.GroupId);
        return WithLocksAsync(async token =>
        {
            var requestedAt = DateTimeOffset.UtcNow;
            var previous = await ReadAndReconcileAsync(token);
            ThrowIfPending();
            if (previous.GroupId == groupId.ToString())
                return new SetActiveGroupResultDto(true, false, previous.GroupId, previous,
                    requestedAt, DateTimeOffset.UtcNow);

            if (!await transport.IsMemberAsync(groupId, token))
                throw new ActiveGroupException("not_group_member", "Munibot is not a member of the requested group.");
            token.ThrowIfCancellationRequested();
            var sessionId = RequireSession();
            _pending = (groupId, sessionId);
            // Once sent, activation cannot be cancelled at the simulator. Retain both locks
            // through confirmation even when the HTTP caller disconnects.
            using var completionTimeout = new CancellationTokenSource(timeout);
            try
            {
                var activated = await transport.ActivateAsync(groupId, completionTimeout.Token);
                if (RequireSession() != sessionId || activated.GroupId != groupId.ToString())
                    throw new ActiveGroupException("group_change_unconfirmed", "The requested active group was not confirmed.", true);
                _pending = null;
                return new SetActiveGroupResultDto(true, true, previous.GroupId, activated,
                    requestedAt, DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException)
            {
                throw new ActiveGroupException("group_change_timeout",
                    "Active group confirmation timed out. Read the active group before retrying; inventory is held until the change is confirmed or the bot reconnects.", true);
            }
            catch (ActiveGroupException ex)
            {
                throw new ActiveGroupException(ex.Code, ex.Message, true);
            }
        }, ct);
    }

    // The caller already holds inventoryLock. Do not acquire it again here.
    public async Task EnsureSettledForInventoryAsync(CancellationToken ct)
    {
        if (_pending is null) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            await ReadAndReconcileAsync(deadline.Token);
            ThrowIfPending();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ActiveGroupException("group_change_unconfirmed",
                "Inventory is held because an earlier active-group change has not been confirmed. Read the active group or reconnect the bot.", true);
        }
    }

    private async Task<BotActiveGroupDto> ReadAndReconcileAsync(CancellationToken ct)
    {
        var sessionId = RequireSession();
        if (_pending?.SessionId != sessionId) _pending = null;
        var state = await transport.ReadAsync(ct);
        if (RequireSession() != sessionId)
            throw new ActiveGroupException("bot_reconnected", "The bot reconnected during the active-group query. Read the active group again.");
        if (state.GroupId == _pending?.GroupId.ToString()) _pending = null;
        return state;
    }

    private UUID RequireSession() => transport.SessionId
        ?? throw new ActiveGroupException("bot_offline", "Munibot is not logged in.");

    private void ThrowIfPending()
    {
        if (_pending is not null)
            throw new ActiveGroupException("group_change_unconfirmed",
                "An earlier active-group change is still unconfirmed. Read the active group or reconnect the bot before changing groups or inventory.", true);
    }

    private async Task<T> WithLocksAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            await teleportLock.WaitAsync(deadline.Token);
            try
            {
                await inventoryLock.WaitAsync(deadline.Token);
                try { return await action(deadline.Token); }
                finally { inventoryLock.Release(); }
            }
            finally { teleportLock.Release(); }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ActiveGroupException("group_query_timeout", "Timed out waiting to query the bot's active group. No activation request was sent.");
        }
    }
}

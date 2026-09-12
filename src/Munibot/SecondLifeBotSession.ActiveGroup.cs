namespace Munibot;

public sealed partial class SecondLifeBotSession
{
    private ActiveGroupController? _activeGroups;
    private ActiveGroupController ActiveGroups => LazyInitializer.EnsureInitialized(ref _activeGroups,
        () => new ActiveGroupController(new GridActiveGroupTransport(_client), _teleportLock, _inventoryLock,
            TimeSpan.FromSeconds(config.Api.GroupOperationTimeoutSeconds)));

    public IActiveGroupService ActiveGroupService => ActiveGroups;

    private Task EnsureActiveGroupSettledAsync(CancellationToken ct) =>
        _activeGroups?.EnsureSettledForInventoryAsync(ct) ?? Task.CompletedTask;
}

namespace Munibot;

public sealed record SetActiveGroupRequestDto(string? GroupId);

public sealed record BotActiveGroupDto(
    string GroupId, string GroupName, string GroupTitle, string GroupPowers,
    DateTimeOffset RetrievedAt, string? PendingGroupId = null);

public sealed record SetActiveGroupResultDto(
    bool Success, bool Changed, string PreviousGroupId, BotActiveGroupDto ActiveGroup,
    DateTimeOffset RequestedAt, DateTimeOffset CompletedAt);

public sealed class ActiveGroupException(string code, string message, bool outcomeUnknown = false)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
    public bool OutcomeUnknown { get; } = outcomeUnknown;
}

public interface IActiveGroupService
{
    Task<BotActiveGroupDto> GetActiveGroupAsync(CancellationToken ct);
    Task<SetActiveGroupResultDto> SetActiveGroupAsync(SetActiveGroupRequestDto request, CancellationToken ct);
}

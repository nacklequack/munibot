namespace Munibot;

public sealed record GroupRosterDto(
    string GroupId,
    string RequestId,
    int MemberCount,
    DateTimeOffset RequestedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyList<GroupMemberDto> Members);

public sealed record GroupMemberDto(
    string AvatarId,
    string? Title,
    string? OnlineStatus,
    bool IsOwner,
    int Contribution,
    string Powers);

public sealed record GroupMemberPresenceDto(
    string GroupId,
    string AvatarId,
    bool Present,
    int MemberCount,
    GroupMemberDto? Member,
    DateTimeOffset RequestedAt,
    DateTimeOffset CompletedAt);

public sealed record HealthDto(
    bool Online,
    string? AgentId,
    string? CurrentSimulator);

public sealed record ReadyDto(
    bool Ready,
    bool Online,
    string? AgentId,
    string? CurrentSimulator,
    string? Reason);

public sealed record ProblemDetailsDto(
    string Error,
    int StatusCode);

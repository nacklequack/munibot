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

public sealed record Vector3Dto(
    float X,
    float Y,
    float Z);

public sealed record BotLocationDto(
    bool Online,
    string? AgentId,
    string? CurrentSimulator,
    Vector3Dto? Position,
    DateTimeOffset RetrievedAt);

public sealed record TeleportRequestDto(
    string? Region,
    Vector3Dto? Position);

public sealed record TeleportResultDto(
    bool Success,
    string Region,
    Vector3Dto Position,
    string? CurrentSimulator,
    DateTimeOffset RequestedAt,
    DateTimeOffset CompletedAt);

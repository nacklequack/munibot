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

public sealed record GroupBanListDto(
    string GroupId,
    int BanCount,
    DateTimeOffset RequestedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyList<GroupBanEntryDto> Bans);

public sealed record GroupBanEntryDto(
    string AvatarId,
    DateTime BannedAt);

public sealed record GroupOperationResultDto(
    string GroupId,
    string AvatarId,
    string Operation,
    bool Success,
    DateTimeOffset RequestedAt,
    DateTimeOffset CompletedAt);

public sealed record GroupInviteRequestDto(
    string? AvatarId,
    List<string> RoleIds);

public sealed record GroupMemberRolesDto(
    string GroupId,
    string AvatarId,
    IReadOnlyList<string> Roles,
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

public sealed record SendInstantMessageRequestDto(
    string? AvatarId,
    string? Message);

public sealed record SendInstantMessageResultDto(
    string AvatarId,
    bool Success,
    DateTimeOffset RequestedAt,
    DateTimeOffset CompletedAt);

public sealed record SendLocalChatRequestDto(
    string? Message,
    int? Channel,
    string? ChatType);

public sealed record SendLocalChatResultDto(
    bool Success,
    int Channel,
    string ChatType,
    DateTimeOffset RequestedAt,
    DateTimeOffset CompletedAt);

public sealed record AvatarNameResolutionRequestDto(
    List<string> Names);

public sealed record AvatarKeyResolutionRequestDto(
    List<string> AvatarIds);

public sealed record AvatarNameResolutionResponseDto(
    IReadOnlyList<AvatarNameResolutionDto> Results,
    DateTimeOffset RequestedAt,
    DateTimeOffset CompletedAt);

public sealed record AvatarKeyResolutionResponseDto(
    IReadOnlyList<AvatarKeyResolutionDto> Results,
    DateTimeOffset RequestedAt,
    DateTimeOffset CompletedAt);

public sealed record AvatarSearchResponseDto(
    string Query,
    IReadOnlyList<AvatarSearchCandidateDto> Candidates,
    DateTimeOffset RequestedAt,
    DateTimeOffset CompletedAt);

public sealed record AvatarNameResolutionDto(
    string RequestedName,
    string? AvatarId,
    string? AvatarName,
    IReadOnlyList<AvatarSearchCandidateDto> Candidates);

public sealed record AvatarKeyResolutionDto(
    string AvatarId,
    string? AvatarName);

public sealed record AvatarSearchCandidateDto(
    string AvatarId,
    string AvatarName,
    bool? Online = null);

public sealed record ExperiencePreferencesDto(
    IReadOnlyList<string> Allowed,
    IReadOnlyList<string> Blocked,
    DateTimeOffset RetrievedAt);

public sealed record ExperienceOperationResultDto(
    string ExperienceId,
    string Operation,
    bool Changed,
    IReadOnlyList<string> Allowed,
    IReadOnlyList<string> Blocked,
    DateTimeOffset RequestedAt,
    DateTimeOffset CompletedAt);

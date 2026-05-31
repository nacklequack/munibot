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

public sealed record GroupRolesDto(
    string GroupId,
    int RoleCount,
    DateTimeOffset RequestedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyList<GroupRoleDto> Roles);

public sealed record GroupRoleDto(
    string RoleId,
    string Name,
    string Title,
    string? Description,
    string Powers,
    int MemberCount);

public sealed record GroupRoleMemberOperationResultDto(
    string GroupId,
    string RoleId,
    string AvatarId,
    string Operation,
    bool Success,
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

public sealed record NearbyObjectScanResultDto(
    string Simulator,
    Vector3Dto Origin,
    float Radius,
    int ObjectCount,
    DateTimeOffset RequestedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyList<NearbyObjectDto> Objects);

public sealed record NearbyObjectDto(
    string ObjectId,
    uint LocalId,
    string? Name,
    string? Description,
    string? OwnerId,
    string? GroupId,
    Vector3Dto Position,
    Vector3Dto Scale,
    float Distance,
    bool IsAttachment,
    uint ParentId,
    string? Text);

public sealed record ObjectInteractRequestDto(
    string? Action,
    Vector3Dto? SitOffset);

public sealed record ObjectInteractResultDto(
    string ObjectId,
    uint LocalId,
    string Action,
    string? Name,
    Vector3Dto Position,
    float Distance,
    bool Success,
    DateTimeOffset RequestedAt,
    DateTimeOffset CompletedAt);

public sealed record EstateListDto(
    string EntryType,
    string AnchorRegion,
    int EntryCount,
    DateTimeOffset RequestedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyList<EstateListEntryDto> Entries);

public sealed record EstateListEntryDto(
    string AvatarId);

public sealed record EstateOperationRequestDto(
    string? AnchorRegion,
    bool? AllEstates);

public sealed record EstateOperationResultDto(
    string EntryType,
    string Action,
    string AvatarId,
    string AnchorRegion,
    bool AllEstates,
    bool Success,
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

public sealed record InventoryItemDto(
    string ItemId,
    string? AssetId,
    string Name,
    string AssetType,
    string InventoryType,
    string ParentId,
    string OwnerId,
    string? Description);

public sealed record InventoryGiveRequestDto(
    string? AvatarId,
    string? ItemId,
    string? ItemPath,
    string? ItemName,
    string? AssetType,
    bool? DoEffect);

public sealed record InventoryGiveResultDto(
    string AvatarId,
    string ItemId,
    string ItemName,
    string AssetType,
    bool Success,
    bool DoEffect,
    DateTimeOffset RequestedAt,
    DateTimeOffset CompletedAt);

public sealed record InventoryRezRequestDto(
    string? ItemId,
    string? ItemPath,
    string? Region,
    Vector3Dto? Position,
    int? Count,
    bool? ConfirmRez);

public sealed record InventoryRezResultDto(
    string ItemId,
    string ItemName,
    string Region,
    Vector3Dto Position,
    int Count,
    IReadOnlyList<string> RequestIds,
    bool Success,
    DateTimeOffset RequestedAt,
    DateTimeOffset CompletedAt);

public sealed record TextureUploadRequestDto(
    string? Name,
    string? Description,
    string? TextureDataBase64,
    string? TextureDataContentType,
    bool? ConfirmUploadFee);

public sealed record TextureUploadResultDto(
    string ItemId,
    string AssetId,
    string Name,
    bool Success,
    string? Status,
    int BytesUploaded,
    int ExpectedUploadCostLinden,
    DateTimeOffset RequestedAt,
    DateTimeOffset CompletedAt);

public sealed record AccountHistoryResponseDto(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int TransactionCount,
    DateTimeOffset RequestedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyList<AccountHistoryTransactionDto> Transactions);

public sealed record AccountHistoryTransactionDto(
    string TransactionId,
    string? Type,
    string? Description,
    string? Resident,
    DateTimeOffset OccurredAtUtc,
    uint EndBalance,
    int? InferredAmountDelta);

public sealed record WalletBalanceDto(
    int Balance,
    string AgentId,
    DateTimeOffset RequestedAt,
    DateTimeOffset CompletedAt);

public sealed record WalletPayRequestDto(
    string? AvatarId,
    int? Amount,
    string? Description,
    bool? ConfirmPayment);

public sealed record WalletPayResultDto(
    string AvatarId,
    int Amount,
    bool Success,
    string? TransactionId,
    int? Balance,
    string? ResponseDescription,
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

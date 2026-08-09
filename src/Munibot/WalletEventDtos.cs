namespace Munibot;

public sealed record WalletEventDto(
    bool Success,
    int? Balance,
    int? Amount,
    string TransactionId,
    string? SourceAvatarUuid,
    string? TargetAvatarUuid,
    string? TransactionType,
    string? Description,
    DateTimeOffset OccurredAtUtc,
    string? BotAgentUuid = null);

public sealed record WalletEventDeliveryResultDto(
    bool Enabled,
    bool Delivered,
    int Attempts,
    string? Status);

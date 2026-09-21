namespace Munibot;

public sealed record TaskInventoryInspectRequestDto(string Region, Vector3Dto Position, IReadOnlyList<string> Names);

public sealed record TaskInventoryWriteRequestDto(
    string Region, Vector3Dto Position, string ContentType, string SourceDataBase64,
    string ExpectedSha256, string RequestId, Guid? ExpectedExperienceId = null);

public sealed record TaskInventoryItemDto(
    string Name, string ItemId, string AssetId, string ContentType,
    bool CanModify, bool CanCopy, bool CanTransfer, bool? Running, string SourceSha256, Guid? ExperienceId = null);

public sealed record TaskInventoryInspectResultDto(string ObjectUuid, IReadOnlyList<TaskInventoryItemDto> Items);

public sealed record TaskInventoryWriteResultDto(
    string RequestId, bool Success, bool UploadSucceeded, bool? Compiled,
    IReadOnlyList<string> CompilationMessages, TaskInventoryItemDto? Item,
    string? ErrorCode, string? Error, bool Retryable, bool OutcomeUnknown = false);

public sealed record TaskInventoryErrorDto(string ErrorCode, string Error, bool Retryable, bool OutcomeUnknown,
    TaskInventorySourceDiagnosticsDto? SourceDiagnostics);

public sealed class TaskInventoryException(string code, string message, bool retryable = false, bool outcomeUnknown = false,
    TaskInventorySourceDiagnosticsDto? sourceDiagnostics = null)
    : Exception(message)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
    public bool OutcomeUnknown { get; } = outcomeUnknown;
    public TaskInventorySourceDiagnosticsDto? SourceDiagnostics { get; } = sourceDiagnostics;
}

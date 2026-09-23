namespace Munibot;

public static class TaskInventoryEndpoints
{
    public static void MapTaskInventoryEndpoints(this WebApplication app)
    {
        app.MapPost("/api/objects/{objectUuid}/inventory/inspect", async (string objectUuid,
            TaskInventoryInspectRequestDto request, SecondLifeBotSession session, ILoggerFactory loggerFactory, CancellationToken ct) =>
            await ExecuteAsync(() => session.InspectTaskInventoryAsync(objectUuid, request, ct),
                loggerFactory.CreateLogger("Munibot.TaskInventory"), "inspect", objectUuid, null, request.Names))
            .RequireMunibotScope(AuthScopes.TaskInventoryWrite);
        app.MapPut("/api/objects/{objectUuid}/inventory/items/{inventoryName}", async (string objectUuid, string inventoryName,
            TaskInventoryWriteRequestDto request, SecondLifeBotSession session, ILoggerFactory loggerFactory, CancellationToken ct) =>
            await ExecuteAsync(() => session.WriteTaskInventoryAsync(objectUuid, inventoryName, request, ct),
                loggerFactory.CreateLogger("Munibot.TaskInventory"), "write", objectUuid, inventoryName, null))
            .RequireMunibotScope(AuthScopes.TaskInventoryWrite);
    }

    private static async Task<IResult> ExecuteAsync<T>(Func<Task<T>> action, ILogger logger, string stage,
        string objectUuid, string? inventoryName, IReadOnlyList<string>? requestedNames)
    {
        logger.LogDebug("Task inventory {Stage} started for {ObjectUuid}; item={InventoryName}; requested={RequestedNames}",
            stage, objectUuid, inventoryName, requestedNames is null ? null : string.Join("|", requestedNames));
        try
        {
            var result = await action();
            if (result is TaskInventoryWriteResultDto write && (!write.Success || !write.UploadSucceeded || write.OutcomeUnknown))
                logger.LogWarning("Task inventory {Stage} returned an unverified result for {ObjectUuid}; item={InventoryName}; " +
                    "code={ErrorCode}; success={Success}; uploadSucceeded={UploadSucceeded}; outcomeUnknown={OutcomeUnknown}",
                    stage, objectUuid, inventoryName, write.ErrorCode, write.Success, write.UploadSucceeded, write.OutcomeUnknown);
            return Results.Ok(result);
        }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (TaskInventoryException ex)
        {
            logger.LogWarning("Task inventory {Stage} failed for {ObjectUuid}; item={InventoryName}; requested={RequestedNames}; " +
                "sourceItem={SourceItemName}; sourceItemId={SourceItemId}; code={ErrorCode}; transferStatus={TransferStatus}",
                stage, objectUuid, inventoryName, requestedNames is null ? null : string.Join("|", requestedNames),
                ex.SourceDiagnostics?.ItemName, ex.SourceDiagnostics?.ItemId, ex.Code, ex.SourceDiagnostics?.TransferStatus);
            return ErrorResult(ex);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Task inventory {Stage} transport failed for {ObjectUuid}; item={InventoryName}; requested={RequestedNames}",
                stage, objectUuid, inventoryName, requestedNames is null ? null : string.Join("|", requestedNames));
            return Results.Json(new { errorCode = "transport_failed", error = "Simulator communication failed. Inspect before retrying.", retryable = true, outcomeUnknown = true }, statusCode: 503);
        }
    }

    public static IResult ErrorResult(TaskInventoryException error) => Results.Json(
        new TaskInventoryErrorDto(error.Code, error.Message, error.Retryable, error.OutcomeUnknown, error.SourceDiagnostics),
        statusCode: error.Retryable ? 503 : 422);

    public static bool IsSensitivePath(PathString path) =>
        path.StartsWithSegments("/api/objects", StringComparison.OrdinalIgnoreCase) &&
        path.Value?.Contains("/inventory/", StringComparison.OrdinalIgnoreCase) == true;
}

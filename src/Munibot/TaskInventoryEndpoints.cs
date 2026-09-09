namespace Munibot;

public static class TaskInventoryEndpoints
{
    public static void MapTaskInventoryEndpoints(this WebApplication app)
    {
        app.MapPost("/api/objects/{objectUuid}/inventory/inspect", async (string objectUuid,
            TaskInventoryInspectRequestDto request, SecondLifeBotSession session, CancellationToken ct) =>
            await ExecuteAsync(() => session.InspectTaskInventoryAsync(objectUuid, request, ct)))
            .RequireMunibotScope(AuthScopes.TaskInventoryWrite);
        app.MapPut("/api/objects/{objectUuid}/inventory/items/{inventoryName}", async (string objectUuid, string inventoryName,
            TaskInventoryWriteRequestDto request, SecondLifeBotSession session, CancellationToken ct) =>
            await ExecuteAsync(() => session.WriteTaskInventoryAsync(objectUuid, inventoryName, request, ct)))
            .RequireMunibotScope(AuthScopes.TaskInventoryWrite);
    }

    private static async Task<IResult> ExecuteAsync<T>(Func<Task<T>> action)
    {
        try { return Results.Ok(await action()); }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (TaskInventoryException ex)
        {
            return Results.Json(new { errorCode = ex.Code, error = ex.Message, retryable = ex.Retryable, outcomeUnknown = ex.OutcomeUnknown },
                statusCode: ex.Retryable ? 503 : 422);
        }
        catch (HttpRequestException)
        {
            return Results.Json(new { errorCode = "transport_failed", error = "Simulator communication failed. Inspect before retrying.", retryable = true, outcomeUnknown = true }, statusCode: 503);
        }
    }

    public static bool IsSensitivePath(PathString path) =>
        path.StartsWithSegments("/api/objects", StringComparison.OrdinalIgnoreCase) &&
        path.Value?.Contains("/inventory/", StringComparison.OrdinalIgnoreCase) == true;
}

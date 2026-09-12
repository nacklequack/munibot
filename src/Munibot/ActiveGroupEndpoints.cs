namespace Munibot;

public static class ActiveGroupEndpoints
{
    public static void MapActiveGroupEndpoints(this WebApplication app)
    {
        app.MapGet("/api/bot/active-group", async (IActiveGroupService service, CancellationToken ct) =>
            await ExecuteAsync(() => service.GetActiveGroupAsync(ct)))
            .RequireMunibotScope(AuthScopes.BotOwner);
        app.MapPut("/api/bot/active-group", async (SetActiveGroupRequestDto request, IActiveGroupService service, CancellationToken ct) =>
            await ExecuteAsync(() => service.SetActiveGroupAsync(request, ct)))
            .RequireMunibotScope(AuthScopes.BotOwner);
    }

    public static bool IsActiveGroupPath(PathString path) =>
        path.Equals(new PathString("/api/bot/active-group"), StringComparison.OrdinalIgnoreCase) ||
        path.Equals(new PathString("/api/bot/active-group/"), StringComparison.OrdinalIgnoreCase);

    private static async Task<IResult> ExecuteAsync<T>(Func<Task<T>> action)
    {
        try { return Results.Ok(await action()); }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (ActiveGroupException ex)
        {
            return Results.Json(new { errorCode = ex.Code, error = ex.Message, outcomeUnknown = ex.OutcomeUnknown },
                statusCode: ex.Code == "not_group_member" ? 422 : 503);
        }
    }
}

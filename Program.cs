using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Munibot;

if (CliOptions.IsHelpRequested(args))
{
    CliOptions.PrintUsage();
    return 0;
}

try
{
    var configPath = CliOptions.GetConfigPath(args);
    var botConfig = BotConfig.Load(configPath);

    var builder = WebApplication.CreateBuilder(args);
    builder.Logging.ClearProviders();
    builder.Logging.AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
    });

    builder.Services.AddSingleton(botConfig);
    builder.Services.AddSingleton<SecondLifeBotSession>();
    builder.Services.AddHostedService<MunibotHostedService>();

    var app = builder.Build();

    app.UseMiddleware<RequestDiagnosticsMiddleware>();

    app.MapGet("/health", (SecondLifeBotSession session) =>
        Results.Ok(new HealthDto(session.IsOnline, session.IsOnline ? session.AgentId : null, session.CurrentSimulator)));

    app.MapGet("/ready", (SecondLifeBotSession session) =>
    {
        var ready = session.IsOnline;
        var dto = new ReadyDto(
            ready,
            session.IsOnline,
            session.IsOnline ? session.AgentId : null,
            session.CurrentSimulator,
            ready ? null : session.LastDisconnectReason ?? "Munibot is not logged in.");

        return ready
            ? Results.Ok(dto)
            : Results.Json(dto, statusCode: StatusCodes.Status503ServiceUnavailable);
    });

    app.MapGet("/api/bot/location", (SecondLifeBotSession session) =>
        Results.Ok(session.GetLocation()))
        .RequireMunibotScope(AuthScopes.BotOwner);

    app.MapPost("/api/bot/teleport", async (
        TeleportRequestDto request,
        SecondLifeBotSession session,
        CancellationToken cancellationToken) =>
    {
        try
        {
            return Results.Ok(await session.TeleportAsync(request, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }).RequireMunibotScope(AuthScopes.BotTeleport);

    app.MapPost("/api/avatars/resolve-names", async (
        AvatarNameResolutionRequestDto request,
        SecondLifeBotSession session,
        CancellationToken cancellationToken) =>
    {
        try
        {
            return Results.Ok(await session.ResolveAvatarNamesAsync(request, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (TimeoutException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status504GatewayTimeout);
        }
    }).RequireMunibotScope(AuthScopes.AvatarResolve);

    app.MapPost("/api/avatars/resolve-keys", async (
        AvatarKeyResolutionRequestDto request,
        SecondLifeBotSession session,
        CancellationToken cancellationToken) =>
    {
        try
        {
            return Results.Ok(await session.ResolveAvatarKeysAsync(request, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (TimeoutException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status504GatewayTimeout);
        }
    }).RequireMunibotScope(AuthScopes.AvatarResolve);

    app.MapGet("/api/avatars/search", async (
        string query,
        SecondLifeBotSession session,
        CancellationToken cancellationToken) =>
    {
        try
        {
            return Results.Ok(await session.SearchPeopleAsync(query, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (TimeoutException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status504GatewayTimeout);
        }
    }).RequireMunibotScope(AuthScopes.AvatarResolve);

    app.MapGet("/api/groups/{groupUuid}/members", async (
        string groupUuid,
        SecondLifeBotSession session,
        CancellationToken cancellationToken) =>
    {
        try
        {
            return Results.Ok(await session.GetGroupRosterAsync(groupUuid, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (TimeoutException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status504GatewayTimeout);
        }
    }).RequireMunibotScope(AuthScopes.RosterRead);

    app.MapGet("/api/groups/{groupUuid}/members/{avatarUuid}", async (
        string groupUuid,
        string avatarUuid,
        SecondLifeBotSession session,
        CancellationToken cancellationToken) =>
    {
        if (!OpenMetaverse.UUID.TryParse(avatarUuid, out var avatarId) || avatarId == OpenMetaverse.UUID.Zero)
        {
            return Results.BadRequest(new { error = "A valid Second Life avatar UUID is required." });
        }

        try
        {
            var roster = await session.GetGroupRosterAsync(groupUuid, cancellationToken);
            var member = roster.Members.FirstOrDefault(m =>
                string.Equals(m.AvatarId, avatarId.ToString(), StringComparison.OrdinalIgnoreCase));

            return Results.Ok(new GroupMemberPresenceDto(
                roster.GroupId,
                avatarId.ToString(),
                member is not null,
                roster.MemberCount,
                member,
                roster.RequestedAt,
                roster.CompletedAt));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (TimeoutException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status504GatewayTimeout);
        }
    }).RequireMunibotScope(AuthScopes.RosterRead);

    await app.RunAsync();
    return 0;
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Console.Error.WriteLine($"Munibot startup failed: {ex.Message}");
    return 1;
}

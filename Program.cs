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
    builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
    builder.Logging.AddFilter("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.Warning);
    builder.Logging.AddFilter("Microsoft.AspNetCore.Http.Result", LogLevel.Warning);

    builder.Services.AddSingleton(botConfig);
    builder.Services.AddSingleton<SecondLifeBotSession>();
    builder.Services.AddSingleton<ISecondLifeAccountHistoryClient, SecondLifeAccountHistoryClient>();
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

    app.MapGet("/api/experiences/preferences", async (
        SecondLifeBotSession session,
        CancellationToken cancellationToken) =>
    {
        try
        {
            return Results.Ok(await session.GetExperiencePreferencesAsync(cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }).RequireMunibotScope(AuthScopes.BotOwner);

    app.MapPost("/api/experiences/{experienceUuid}:allow", async (
        string experienceUuid,
        SecondLifeBotSession session,
        CancellationToken cancellationToken) =>
    {
        try
        {
            return Results.Ok(await session.AllowExperienceAsync(experienceUuid, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }).RequireMunibotScope(AuthScopes.BotOwner);

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

    app.MapPost("/api/ims", async (
        SendInstantMessageRequestDto request,
        SecondLifeBotSession session,
        CancellationToken cancellationToken) =>
    {
        try
        {
            return Results.Ok(await session.SendInstantMessageAsync(request, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }).RequireMunibotScope(AuthScopes.ImSend);

    app.MapPost("/api/local-chat", async (
        SendLocalChatRequestDto request,
        SecondLifeBotSession session,
        CancellationToken cancellationToken) =>
    {
        try
        {
            return Results.Ok(await session.SendLocalChatAsync(request, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }).RequireMunibotScope(AuthScopes.LocalChatSend, AuthScopes.BotOwner);

    app.MapGet("/api/inventory/items/by-path", async (
        string path,
        SecondLifeBotSession session,
        CancellationToken cancellationToken) =>
    {
        try
        {
            return Results.Ok(await session.GetInventoryItemByPathAsync(path, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (TimeoutException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status504GatewayTimeout);
        }
    }).RequireMunibotScope(AuthScopes.InventoryGive);

    app.MapGet("/api/inventory/items/{itemUuid}", async (
        string itemUuid,
        SecondLifeBotSession session,
        CancellationToken cancellationToken) =>
    {
        try
        {
            return Results.Ok(await session.GetInventoryItemByIdAsync(itemUuid, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (TimeoutException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status504GatewayTimeout);
        }
    }).RequireMunibotScope(AuthScopes.InventoryGive);

    app.MapPost("/api/inventory/give", async (
        InventoryGiveRequestDto request,
        SecondLifeBotSession session,
        CancellationToken cancellationToken) =>
    {
        try
        {
            return Results.Ok(await session.GiveInventoryItemAsync(request, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (TimeoutException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status504GatewayTimeout);
        }
    }).RequireMunibotScope(AuthScopes.InventoryGive);

    app.MapPost("/api/textures", async (
        TextureUploadRequestDto request,
        SecondLifeBotSession session,
        CancellationToken cancellationToken) =>
    {
        try
        {
            return Results.Ok(await session.UploadTextureAsync(request, cancellationToken));
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
    }).RequireMunibotScope(AuthScopes.TextureUpload);

    app.MapGet("/api/wallet/balance", async (
        SecondLifeBotSession session,
        CancellationToken cancellationToken) =>
    {
        try
        {
            return Results.Ok(await session.GetWalletBalanceAsync(cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (TimeoutException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status504GatewayTimeout);
        }
    }).RequireMunibotScope(AuthScopes.WalletRead);

    app.MapPost("/api/wallet/pay-avatar", async (
        WalletPayRequestDto request,
        SecondLifeBotSession session,
        CancellationToken cancellationToken) =>
    {
        try
        {
            return Results.Ok(await session.PayAvatarAsync(request, cancellationToken));
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
    }).RequireMunibotScope(AuthScopes.WalletPay);

    app.MapGet("/api/wallet/account-history", async (
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        ISecondLifeAccountHistoryClient accountHistoryClient,
        CancellationToken cancellationToken) =>
    {
        try
        {
            return Results.Ok(await accountHistoryClient.GetTransactionsAsync(fromUtc, toUtc, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status504GatewayTimeout);
        }
    }).RequireMunibotScope(AuthScopes.WalletHistory);

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

    app.MapGet("/api/groups/{groupUuid}/bans", async (
        string groupUuid,
        SecondLifeBotSession session,
        CancellationToken cancellationToken) =>
    {
        try
        {
            return Results.Ok(await session.GetGroupBansAsync(groupUuid, cancellationToken));
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
    }).RequireMunibotScope(AuthScopes.GroupBanRead);

    app.MapPost("/api/groups/{groupUuid}/bans/{avatarUuid}:remove", async (
        string groupUuid,
        string avatarUuid,
        SecondLifeBotSession session,
        CancellationToken cancellationToken) =>
    {
        try
        {
            return Results.Ok(await session.UnbanGroupMemberAsync(groupUuid, avatarUuid, cancellationToken));
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
    }).RequireMunibotScope(AuthScopes.GroupBanWrite);

    app.MapPost("/api/groups/{groupUuid}/invites", async (
        string groupUuid,
        GroupInviteRequestDto request,
        SecondLifeBotSession session,
        CancellationToken cancellationToken) =>
    {
        try
        {
            return Results.Ok(await session.InviteGroupMemberAsync(groupUuid, request, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }).RequireMunibotScope(AuthScopes.GroupInvite);

    app.MapPost("/api/groups/{groupUuid}/members/{avatarUuid}:eject", async (
        string groupUuid,
        string avatarUuid,
        SecondLifeBotSession session,
        CancellationToken cancellationToken) =>
    {
        try
        {
            return Results.Ok(await session.EjectGroupMemberAsync(groupUuid, avatarUuid, cancellationToken));
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
    }).RequireMunibotScope(AuthScopes.GroupEject);

    app.MapGet("/api/groups/{groupUuid}/members/{avatarUuid}/roles", async (
        string groupUuid,
        string avatarUuid,
        SecondLifeBotSession session,
        CancellationToken cancellationToken) =>
    {
        try
        {
            return Results.Ok(await session.GetGroupMemberRolesAsync(groupUuid, avatarUuid, cancellationToken));
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
    }).RequireMunibotScope(AuthScopes.GroupRolesRead);

    await app.RunAsync();
    return 0;
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Console.Error.WriteLine($"Munibot startup failed: {ex.Message}");
    return 1;
}

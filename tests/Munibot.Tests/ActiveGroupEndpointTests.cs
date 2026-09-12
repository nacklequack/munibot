using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenMetaverse;

namespace Munibot.Tests;

public sealed class ActiveGroupEndpointTests
{
    [Theory]
    [InlineData("GET", "/api/bot/active-group")]
    [InlineData("PUT", "/api/bot/active-group")]
    [InlineData("GET", "/API/BOT/ACTIVE-GROUP/")]
    [InlineData("PUT", "/API/BOT/ACTIVE-GROUP/")]
    public async Task RequiresConfiguredOwnerTokenEvenInAnonymousDevelopmentMode(string method, string path)
    {
        await using var host = await Host.StartAsync();
        using var client = host.CreateClient();
        using var anonymous = await SendAsync(client, method, path);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Add("X-Munibot-Token", "reader-token");
        using var wrongScope = await SendAsync(client, method, path);
        Assert.Equal(HttpStatusCode.Forbidden, wrongScope.StatusCode);
        Assert.Equal(0, host.Grid.Reads);
        client.DefaultRequestHeaders.Remove("X-Munibot-Token");
        client.DefaultRequestHeaders.Add("X-Munibot-Token", "owner-token");
        using var owner = await SendAsync(client, method, path);
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
        Assert.True(host.Grid.Reads > 0);

        await using var unconfigured = await Host.StartAsync(noTokens: true);
        using var unconfiguredClient = unconfigured.CreateClient();
        using var noToken = await SendAsync(unconfiguredClient, method, path);
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
        Assert.Equal(0, unconfigured.Grid.Reads);
    }

    [Fact]
    public async Task InvalidInputAndNonMembershipReturnActionableErrors()
    {
        await using var host = await Host.StartAsync();
        using var client = host.CreateClient(owner: true);
        using var invalid = await client.PutAsJsonAsync("/api/bot/active-group", new { groupId = "invalid" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        host.Grid.Member = false;
        using var nonMember = await client.PutAsJsonAsync("/api/bot/active-group", new { groupId = UUID.Random().ToString() });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, nonMember.StatusCode);
        using var body = JsonDocument.Parse(await nonMember.Content.ReadAsStringAsync());
        Assert.Equal("not_group_member", body.RootElement.GetProperty("errorCode").GetString());
        Assert.False(body.RootElement.GetProperty("outcomeUnknown").GetBoolean());
        Assert.Empty(host.Grid.Activations);
    }

    [Fact]
    public async Task UncertainActivationReturns503AndGetExposesPendingGroup()
    {
        await using var host = await Host.StartAsync();
        using var client = host.CreateClient(owner: true);
        var requested = UUID.Random().ToString();
        host.Grid.Activate = (_, _) => throw new OperationCanceledException();
        using var response = await client.PutAsJsonAsync("/api/bot/active-group", new { groupId = requested });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(error.RootElement.GetProperty("outcomeUnknown").GetBoolean());
        var state = await client.GetFromJsonAsync<BotActiveGroupDto>("/api/bot/active-group");
        Assert.Equal(requested, state!.PendingGroupId);
        Assert.NotEqual(requested, state.GroupId);
        var text = await client.GetStringAsync("/api/bot/active-group");
        Assert.DoesNotContain("sessionId", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("owner-token", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OperatorPowerShellConsumerReachesGetAndPutAndVerifiesConfirmation()
    {
        await using var host = await Host.StartAsync();
        var groupId = UUID.Random().ToString();
        var read = await RunOperatorAsync(host.Address);
        Assert.Equal(host.Grid.State.GroupId, read.RootElement.GetProperty("groupId").GetString());
        using var changed = await RunOperatorAsync(host.Address, groupId);
        Assert.True(changed.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(groupId, changed.RootElement.GetProperty("activeGroup").GetProperty("groupId").GetString());
        Assert.Equal(UUID.Parse(groupId), Assert.Single(host.Grid.Activations));
        read.Dispose();
    }

    private static async Task<JsonDocument> RunOperatorAsync(string address, string? groupId = null)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Munibot.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var start = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(root.FullName, "scripts", "bot-active-group.ps1"), "-BotUrl", address })
            start.ArgumentList.Add(arg);
        if (groupId is not null)
        {
            start.ArgumentList.Add("-GroupId");
            start.ArgumentList.Add(groupId);
        }
        start.Environment["MUNIBOT_OWNER_TOKEN"] = "owner-token";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(deadline.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        Assert.True(process.ExitCode == 0, await errors);
        return JsonDocument.Parse(await output);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string path) =>
        method == "GET" ? client.GetAsync(path) : client.PutAsJsonAsync(path, new { groupId = UUID.Random().ToString() });

    private sealed class Host(WebApplication app, SemaphoreSlim teleport, SemaphoreSlim inventory,
        FakeActiveGroupTransport grid, string address) : IAsyncDisposable
    {
        public FakeActiveGroupTransport Grid => grid;
        public string Address => address;
        public HttpClient CreateClient(bool owner = false)
        {
            var client = new HttpClient { BaseAddress = new Uri(address) };
            if (owner) client.DefaultRequestHeaders.Add("X-Munibot-Token", "owner-token");
            return client;
        }
        public static async Task<Host> StartAsync(bool noTokens = false)
        {
            var config = new BotConfig
            {
                Tokens = noTokens ? [] :
                [
                    new() { Id = "owner", Value = "owner-token", Scopes = [AuthScopes.BotOwner] },
                    new() { Id = "reader", Value = "reader-token", Scopes = [AuthScopes.RosterRead] }
                ]
            };
            var teleport = new SemaphoreSlim(1, 1);
            var inventory = new SemaphoreSlim(1, 1);
            var grid = new FakeActiveGroupTransport();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(config);
            builder.Services.AddSingleton<IActiveGroupService>(new ActiveGroupController(grid, teleport, inventory, TimeSpan.FromSeconds(5)));
            var app = builder.Build();
            app.MapActiveGroupEndpoints();
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new Host(app, teleport, inventory, grid, address);
        }
        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
            teleport.Dispose();
            inventory.Dispose();
        }
    }
}

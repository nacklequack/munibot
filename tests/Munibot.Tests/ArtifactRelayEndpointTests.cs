using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using OpenMetaverse;

namespace Munibot.Tests;

public sealed class ArtifactRelayEndpointTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoutesRequireDedicatedScopeEvenWhenOtherApisAllowAnonymousDevelopment(bool noTokens)
    {
        await using var host = await Host.StartAsync(noTokens);
        var requestId = Guid.NewGuid().ToString();
        using var client = host.CreateClient();
        using var anonymous = await client.PutAsJsonAsync($"/api/inventory/artifact-offers/{requestId}", Offer());
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        if (noTokens) return;

        client.DefaultRequestHeaders.Add("X-Munibot-Token", "task-token");
        using var wrongScope = await client.PutAsJsonAsync($"/api/inventory/artifact-offers/{requestId}", Offer());
        Assert.Equal(HttpStatusCode.Forbidden, wrongScope.StatusCode);
        client.DefaultRequestHeaders.Remove("X-Munibot-Token");
        client.DefaultRequestHeaders.Add("X-Munibot-Token", "artifact-token");
        using var allowed = await client.PutAsJsonAsync($"/api/inventory/artifact-offers/{requestId}", Offer());
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(1, host.Service.Registrations);
    }

    [Fact]
    public async Task DeliveryRouteUsesArtifactScopeAndNeverExposesGenericInventoryCopy()
    {
        await using var host = await Host.StartAsync(false);
        using var client = host.CreateClient("artifact-token");
        var objectId = Guid.NewGuid().ToString();
        var requestId = Guid.NewGuid().ToString();
        var masks = new InventoryPermissionMasksDto(1, 2, 3, 4, 5);
        var request = new ArtifactRelayRequestDto("Briarmont", new Vector3Dto(128, 128, 25),
            "HUD Scanner", Guid.NewGuid().ToString(), Guid.Empty.ToString(), "hud-runtime-scanner",
            [new("managed-runtime", Guid.NewGuid().ToString(), "LSLText")], masks);

        using var response = await client.PutAsJsonAsync(
            $"/api/objects/{objectId}/inventory/artifacts/{requestId}", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, host.Service.Relays);
        using var absent = await client.PutAsJsonAsync("/api/inventory/copy-to-object", new { });
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
    }

    private static ArtifactOfferExpectationRequestDto Offer() => new(
        Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "Community Drop Box", "Scanner HUD",
        Guid.NewGuid().ToString(), new(1, 2, 3, 4, 5));

    private sealed class Host(WebApplication app, string address, FakeService service) : IAsyncDisposable
    {
        public FakeService Service => service;

        public HttpClient CreateClient(string? token = null)
        {
            var client = new HttpClient { BaseAddress = new Uri(address) };
            if (token is not null) client.DefaultRequestHeaders.Add("X-Munibot-Token", token);
            return client;
        }

        public static async Task<Host> StartAsync(bool noTokens)
        {
            var config = new BotConfig
            {
                Tokens = noTokens ? [] :
                [
                    new() { Id = "artifact", Value = "artifact-token", Scopes = [AuthScopes.ArtifactRelay] },
                    new() { Id = "task", Value = "task-token", Scopes = [AuthScopes.TaskInventoryWrite] }
                ]
            };
            var service = new FakeService();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(config);
            builder.Services.AddSingleton<IArtifactRelayService>(service);
            var app = builder.Build();
            app.MapArtifactRelayEndpoints();
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            return new Host(app, address, service);
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class FakeService : IArtifactRelayService
    {
        public int Registrations { get; private set; }
        public int Relays { get; private set; }

        public ArtifactOfferStatusDto RegisterArtifactOffer(string requestId,
            ArtifactOfferExpectationRequestDto request)
        {
            Registrations++;
            return new(requestId, "pending", DateTimeOffset.UtcNow.AddMinutes(1), null, null, null, false, false);
        }

        public ArtifactOfferStatusDto GetArtifactOffer(string requestId) =>
            new(requestId, "pending", DateTimeOffset.UtcNow.AddMinutes(1), null, null, null, false, false);

        public Task<ArtifactRelayResultDto> RelayArtifactAsync(string objectUuid, string requestId,
            ArtifactRelayRequestDto request, CancellationToken ct)
        {
            Relays++;
            var item = new ArtifactInventoryItemDto(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(),
                "Scanner HUD", "Object", "Object", Guid.NewGuid().ToString(), Guid.Empty.ToString(), false,
                request.ExpectedTargetPermissions, true, true, true);
            return Task.FromResult(new ArtifactRelayResultDto(requestId, objectUuid,
                request.ExpectedBundleKey, true, false, item, item));
        }
    }
}

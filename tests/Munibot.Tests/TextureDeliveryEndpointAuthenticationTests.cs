using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Munibot.Tests;

public sealed class TextureDeliveryEndpointAuthenticationTests
{
    [Fact]
    public async Task FullPermissionTexture_RequiresInventoryGiveScope()
    {
        var config = new BotConfig { Tokens = [
            new() { Id = "give", Value = "test-give-token", Scopes = [AuthScopes.InventoryGive] },
            new() { Id = "roster", Value = "test-roster-token", Scopes = [AuthScopes.RosterRead] }] };
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(_ => new SecondLifeBotSession(config, NullLogger<SecondLifeBotSession>.Instance, null!, null!));
        await using var app = builder.Build();
        app.MapTextureDeliveryEndpoints();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(address) };
            var request = new FullPermissionTextureRequestDto(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
            using var anonymous = await http.PostAsJsonAsync("/api/inventory/give-texture", request);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            http.DefaultRequestHeaders.Add("X-Munibot-Token", "test-roster-token");
            using var wrong = await http.PostAsJsonAsync("/api/inventory/give-texture", request);
            Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
            http.DefaultRequestHeaders.Remove("X-Munibot-Token");
            http.DefaultRequestHeaders.Add("X-Munibot-Token", "test-give-token");
            using var permitted = await http.PostAsJsonAsync("/api/inventory/give-texture", request);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, permitted.StatusCode);
        }
        finally { await app.StopAsync(); }
    }
}

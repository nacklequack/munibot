using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Munibot.Tests;

public sealed class TrafficEndpointAuthenticationTests
{
    [Theory]
    [InlineData("/api/parcels/11111111-1111-1111-1111-111111111111/traffic")]
    [InlineData("/api/regions/Example%20Region/parcel-inventory")]
    public async Task TrafficRoutes_RequireTrafficReadScope(string path)
    {
        var config = new BotConfig
        {
            Tokens =
            [
                new() { Id = "reader", Value = "test-traffic-token", Scopes = [AuthScopes.TrafficRead] },
                new() { Id = "roster", Value = "test-roster-token", Scopes = [AuthScopes.RosterRead] }
            ]
        };
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(_ => new SecondLifeBotSession(config,
            NullLogger<SecondLifeBotSession>.Instance, null!, null!));
        await using var app = builder.Build();
        app.MapTrafficEndpoints();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(address) };
            using var anonymous = await http.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            http.DefaultRequestHeaders.Add("X-Munibot-Token", "test-roster-token");
            using var wrongScope = await http.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, wrongScope.StatusCode);
            http.DefaultRequestHeaders.Remove("X-Munibot-Token");
            http.DefaultRequestHeaders.Add("X-Munibot-Token", "test-traffic-token");
            using var permitted = await http.GetAsync(path);
            // The request reaches the offline session rather than being rejected by authorization.
            Assert.Equal(HttpStatusCode.ServiceUnavailable, permitted.StatusCode);
        }
        finally { await app.StopAsync(); }
    }
}

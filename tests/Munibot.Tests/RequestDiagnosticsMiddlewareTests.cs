using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Munibot;

namespace Munibot.Tests;

public sealed class RequestDiagnosticsMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_PreservesRequestBodyForDownstreamHandler()
    {
        var config = new BotConfig
        {
            Diagnostics = new BotDiagnosticsConfig
            {
                LogApiCalls = true,
                LogApiBodies = true,
                MaxLoggedBodyBytes = 4096
            }
        };
        string? downstreamBody = null;
        var middleware = new RequestDiagnosticsMiddleware(
            async context =>
            {
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
                downstreamBody = await reader.ReadToEndAsync();
                await context.Response.WriteAsync("{\"ok\":true}");
            },
            config,
            NullLogger<RequestDiagnosticsMiddleware>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "POST";
        httpContext.Request.Path = "/api/test";
        httpContext.Request.ContentType = "application/json";
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"password\":\"secret\",\"ok\":true}"));
        httpContext.Request.ContentLength = httpContext.Request.Body.Length;
        httpContext.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(httpContext);

        Assert.Equal("{\"password\":\"secret\",\"ok\":true}", downstreamBody);
        httpContext.Response.Body.Position = 0;
        using var responseReader = new StreamReader(httpContext.Response.Body, Encoding.UTF8);
        Assert.Equal("{\"ok\":true}", await responseReader.ReadToEndAsync());
    }

    [Fact]
    public async Task InvokeAsync_WhenBodyLoggingDisabled_DoesNotBufferResponse()
    {
        var config = new BotConfig
        {
            Diagnostics = new BotDiagnosticsConfig
            {
                LogApiCalls = true,
                LogApiBodies = false
            }
        };
        var middleware = new RequestDiagnosticsMiddleware(
            context => context.Response.WriteAsync("plain-response"),
            config,
            NullLogger<RequestDiagnosticsMiddleware>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(httpContext);

        httpContext.Response.Body.Position = 0;
        using var responseReader = new StreamReader(httpContext.Response.Body, Encoding.UTF8);
        Assert.Equal("plain-response", await responseReader.ReadToEndAsync());
    }
}

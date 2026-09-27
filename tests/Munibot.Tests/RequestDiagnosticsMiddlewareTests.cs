using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Munibot;

namespace Munibot.Tests;

public sealed class RequestDiagnosticsMiddlewareTests
{
    [Theory]
    [InlineData("POST", "/api/test")]
    [InlineData("GET", "/api/bot/location")]
    public async Task InvokeAsync_PreservesRequestBodyForDownstreamHandler(string method, string path)
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
        httpContext.Request.Method = method;
        httpContext.Request.Path = path;
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

    [Fact]
    public async Task InvokeAsync_ForProbePath_SkipsDiagnosticLog()
    {
        var config = new BotConfig
        {
            Diagnostics = new BotDiagnosticsConfig
            {
                LogApiCalls = true,
                LogApiBodies = true
            }
        };
        var logger = new CapturingLogger<RequestDiagnosticsMiddleware>();
        var middleware = new RequestDiagnosticsMiddleware(
            context => context.Response.WriteAsync("{\"status\":\"ok\"}"),
            config,
            logger);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/health";
        httpContext.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(httpContext);

        Assert.Empty(logger.Messages);
        httpContext.Response.Body.Position = 0;
        using var responseReader = new StreamReader(httpContext.Response.Body, Encoding.UTF8);
        Assert.Equal("{\"status\":\"ok\"}", await responseReader.ReadToEndAsync());
    }

    [Theory]
    [InlineData("/api/inventory/artifact-offers/6f37bec7-a7bb-44cb-8a80-f511807858df")]
    [InlineData("/api/objects/6f37bec7-a7bb-44cb-8a80-f511807858df/inventory/artifacts/992dd7cc-9813-42dd-b759-ceeafc4fd6e7")]
    public async Task InvokeAsync_ForArtifactRelayPath_SkipsPrivateDiagnostics(string path)
    {
        var config = new BotConfig
        {
            Diagnostics = new BotDiagnosticsConfig { LogApiCalls = true, LogApiBodies = true }
        };
        var logger = new CapturingLogger<RequestDiagnosticsMiddleware>();
        var middleware = new RequestDiagnosticsMiddleware(
            context => context.Response.WriteAsync("{\"private\":\"identity\"}"), config, logger);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "PUT";
        httpContext.Request.Path = path;
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"assetId\":\"private\"}"));
        httpContext.Request.ContentLength = httpContext.Request.Body.Length;
        httpContext.Request.ContentType = "application/json";
        httpContext.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(httpContext);

        Assert.Empty(logger.Messages);
    }

    [Theory]
    [InlineData("GET", "/api/bot/location", 200, LogLevel.Debug)]
    [InlineData("GET", "/API/BOT/LOCATION/", 200, LogLevel.Debug)]
    [InlineData("GET", "/api/bot/location", 204, LogLevel.Debug)]
    [InlineData("GET", "/api/bot/location", 302, LogLevel.Information)]
    [InlineData("GET", "/api/bot/location", 401, LogLevel.Information)]
    [InlineData("GET", "/api/bot/location", 403, LogLevel.Information)]
    [InlineData("GET", "/api/bot/location", 500, LogLevel.Information)]
    [InlineData("POST", "/api/bot/location", 200, LogLevel.Information)]
    [InlineData("GET", "/api/bot/location/other", 200, LogLevel.Information)]
    [InlineData("GET", "/api/test", 200, LogLevel.Information)]
    public async Task InvokeAsync_OnlySuccessfulLocationPollsLogAtDebug(
        string method, string path, int statusCode, LogLevel expectedLevel)
    {
        var logger = new CapturingLogger<RequestDiagnosticsMiddleware>();
        var middleware = new RequestDiagnosticsMiddleware(
            context =>
            {
                context.Response.StatusCode = statusCode;
                return context.Response.WriteAsync("response");
            },
            new BotConfig(),
            logger);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = method;
        httpContext.Request.Path = path;
        httpContext.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(httpContext);

        Assert.Equal(expectedLevel, Assert.Single(logger.Levels));
        Assert.Contains($"status={statusCode}", Assert.Single(logger.Messages));
        httpContext.Response.Body.Position = 0;
        using var reader = new StreamReader(httpContext.Response.Body);
        Assert.Equal("response", await reader.ReadToEndAsync());
    }

    [Theory]
    [InlineData("/api/test")]
    [InlineData("/api/bot/location")]
    public async Task InvokeAsync_WhenDownstreamThrows_LogsOnlyFailure(string path)
    {
        var config = new BotConfig
        {
            Diagnostics = new BotDiagnosticsConfig
            {
                LogApiCalls = true,
                LogApiBodies = false
            }
        };
        var logger = new CapturingLogger<RequestDiagnosticsMiddleware>();
        var middleware = new RequestDiagnosticsMiddleware(
            _ => throw new InvalidOperationException("boom"),
            config,
            logger);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "GET";
        httpContext.Request.Path = path;
        httpContext.Response.Body = new MemoryStream();

        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(httpContext));

        var message = Assert.Single(logger.Messages);
        Assert.Equal(LogLevel.Error, Assert.Single(logger.Levels));
        Assert.Contains("failed status=500", message);
        Assert.DoesNotContain(" status=200 ", message);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            Levels.Add(logLevel);
        }
    }
}

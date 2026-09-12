using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenMetaverse;

namespace Munibot.Tests;

public sealed class TaskInventoryErrorResponseTests
{
    [Theory]
    [InlineData(false, 422)]
    [InlineData(true, 503)]
    public async Task ErrorResponsePreservesDiagnosticsAndRetryDisposition(bool retryable, int status)
    {
        var item = new InventoryItem(UUID.Random()) { OwnerID = UUID.Random(), GroupID = UUID.Random(), AssetType = AssetType.LSLText };
        var details = TaskInventorySourceDiagnosticsDto.Capture(UUID.Random(), item.GroupID, UUID.Random(), item)
            with { TransferStatus = retryable ? "Error" : "InsufficientPermissions", TransferSucceeded = false };
        var code = retryable ? "source_unreadable" : "source_permission_denied";
        var error = new TaskInventoryException(code, "Source could not be verified.", retryable, true, details);
        using var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = "/api/objects/00000000-0000-0000-0000-000000000001/inventory/inspect";
        context.Response.Body = new MemoryStream();
        var logger = new CapturingLogger();
        var middleware = new RequestDiagnosticsMiddleware(c => TaskInventoryEndpoints.ErrorResult(error).ExecuteAsync(c),
            new BotConfig { Diagnostics = new() { LogApiCalls = true, LogApiBodies = true } }, logger);

        await middleware.InvokeAsync(context);

        Assert.Empty(logger.Messages);
        Assert.Equal(status, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(code, body.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal(retryable, body.RootElement.GetProperty("retryable").GetBoolean());
        Assert.True(body.RootElement.GetProperty("outcomeUnknown").GetBoolean());
        var observed = body.RootElement.GetProperty("sourceDiagnostics");
        Assert.Equal(details.ItemOwnerId, observed.GetProperty("itemOwnerId").GetString());
        Assert.Equal(details.ActiveGroupId, observed.GetProperty("activeGroupId").GetString());
        Assert.Equal(details.TransferStatus, observed.GetProperty("transferStatus").GetString());
        Assert.False(observed.TryGetProperty("source", out _));
        Assert.False(observed.TryGetProperty("sessionId", out _));
    }

    [Fact]
    public async Task SharingMismatchDetailsReachCallerWithoutHttpBodyLogging()
    {
        var error = Assert.Throws<TaskInventoryException>(() =>
            TaskInventorySharing.VerifyCopy(new InventoryItem(UUID.Random()), UUID.Random()));
        using var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = "PUT";
        context.Request.Path = "/api/objects/00000000-0000-0000-0000-000000000001/inventory/items/probe.lsl";
        context.Response.Body = new MemoryStream();
        var logger = new CapturingLogger();
        var middleware = new RequestDiagnosticsMiddleware(c => TaskInventoryEndpoints.ErrorResult(error).ExecuteAsync(c),
            new BotConfig { Diagnostics = new() { LogApiCalls = true, LogApiBodies = true } }, logger);

        await middleware.InvokeAsync(context);

        Assert.Empty(logger.Messages);
        Assert.Equal(422, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(context.Response.Body);
        var message = body.RootElement.GetProperty("error").GetString();
        Assert.Equal(error.Message, message);
        Assert.Contains("group_id (expected nonzero, observed zero)", message);
        Assert.Contains("group_mask (required 0x0008c000, observed 0x00000000, missing 0x0008c000)", message);
        Assert.False(body.RootElement.GetProperty("retryable").GetBoolean());
        Assert.True(body.RootElement.GetProperty("outcomeUnknown").GetBoolean());
    }

    private sealed class CapturingLogger : ILogger<RequestDiagnosticsMiddleware>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
            => Messages.Add(format(state, error));
    }
}

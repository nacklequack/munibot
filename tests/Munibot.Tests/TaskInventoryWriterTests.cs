using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Munibot.Tests;

public sealed class TaskInventoryWriterTests
{
    private const string Name = "payload.lsl";
    private static readonly byte[] Source = Encoding.UTF8.GetBytes("default { state_entry() {} }\n");
    private static TaskInventoryWriteRequestDto Request(string kind = "script") => new("Test Region", new(10, 10, 25), kind,
        Convert.ToBase64String(Source), TaskInventoryContent.Hash(Source), Guid.NewGuid().ToString());
    private static TaskInventoryItemDto Item(string kind = "script") => new(Name, Guid.NewGuid().ToString(), Guid.NewGuid().ToString(),
        kind, true, true, true, kind == "script" ? false : null, TaskInventoryContent.Hash(Source));

    [Theory]
    [InlineData(true, "script")]
    [InlineData(false, "script")]
    [InlineData(false, "notecard")]
    public async Task WaitsForUploadAndReadbackBeforeSuccess(bool exists, string kind)
    {
        var final = new TaskCompletionSource<TaskInventoryUploadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = Item(kind);
        var target = new FakeTarget { Before = exists ? [item] : [], After = [item], UploadCompletion = final.Task };
        var operation = new TaskInventoryWriter(target).WriteAsync(Name, Request(kind), default);
        Assert.False(operation.IsCompleted);
        Assert.Equal(exists, target.Existing is not null);
        final.SetResult(new(true, kind == "script" ? true : null, [], item.AssetId));
        var result = await operation;
        Assert.True(result.Success);
        Assert.Equal(item.ItemId, result.Item!.ItemId);
        Assert.Equal(2, target.Reads);
    }

    [Fact]
    public async Task EmptyExistingItemIsUpdatedByItsIdentityWithoutCreatingAnotherCopy()
    {
        var completed = Item();
        var empty = completed with { AssetId = Guid.Empty.ToString(), Running = null, SourceSha256 = "" };
        var target = new FakeTarget { Before = [empty], After = [completed] };
        var result = await new TaskInventoryWriter(target).WriteAsync(Name, Request(), default);
        Assert.True(result.Success);
        Assert.Equal(empty.ItemId, target.Existing!.ItemId);
        Assert.Equal(empty.ItemId, result.Item!.ItemId);
    }

    [Fact]
    public async Task InventoryTimeoutNeverCreatesAnItem()
    {
        var target = new FakeTarget { ReadFailure = new TimeoutException() };
        await Assert.ThrowsAsync<TimeoutException>(() => new TaskInventoryWriter(target).WriteAsync(Name, Request(), default));
        Assert.Equal(0, target.Uploads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PermissionDenialReportsWhetherAnUploadAlreadyHappened(bool afterUpload)
    {
        var diagnostics = TaskInventorySourceDiagnosticsDto.Capture(OpenMetaverse.UUID.Random(), OpenMetaverse.UUID.Zero,
            OpenMetaverse.UUID.Random(), new OpenMetaverse.InventoryItem(OpenMetaverse.UUID.Random()));
        var denied = new TaskInventoryException("source_permission_denied", "Source access was denied.", sourceDiagnostics: diagnostics);
        var target = new FakeTarget
        {
            Before = [Item()],
            ReadFailure = afterUpload ? null : denied,
            ReadFailureAfterUpload = afterUpload ? denied : null
        };

        var error = await Assert.ThrowsAsync<TaskInventoryException>(() => new TaskInventoryWriter(target).WriteAsync(Name, Request(), default));

        Assert.Equal("source_permission_denied", error.Code);
        Assert.False(error.Retryable);
        Assert.Equal(afterUpload, error.OutcomeUnknown);
        Assert.Same(diagnostics, error.SourceDiagnostics);
        Assert.Equal(afterUpload ? 1 : 0, target.Uploads);
        Assert.Equal(afterUpload ? 2 : 1, target.Reads);
    }

    [Theory]
    [InlineData("wrong_inventory_type")]
    [InlineData("permission_denied")]
    [InlineData("ambiguous_inventory_name")]
    public async Task InvalidInventoryDoesNotUpload(string error)
    {
        var item = Item();
        var target = new FakeTarget { Before = error switch
        {
            "wrong_inventory_type" => [item with { ContentType = "notecard" }],
            "permission_denied" => [item with { CanCopy = false }],
            _ => [item, item with { ItemId = Guid.NewGuid().ToString() }]
        }};
        var ex = await Assert.ThrowsAsync<TaskInventoryException>(() => new TaskInventoryWriter(target).WriteAsync(Name, Request(), default));
        Assert.Equal(error, ex.Code);
        Assert.Equal(0, target.Uploads);
    }

    [Fact]
    public async Task CompileFailurePreservesDiagnosticsAndCannotReportSuccess()
    {
        var target = new FakeTarget { Before = [Item()], UploadCompletion = Task.FromResult(new TaskInventoryUploadResult(true, false, ["line 3: syntax error"])) };
        var result = await new TaskInventoryWriter(target).WriteAsync(Name, Request(), default);
        Assert.False(result.Success);
        Assert.False(result.Retryable);
        Assert.Equal("compile_failed", result.ErrorCode);
        Assert.Equal("line 3: syntax error", Assert.Single(result.CompilationMessages));
        Assert.Equal(1, target.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExperienceWriteRequiresExactInstalledItemAfterFinalCompletion(bool exists)
    {
        var experience = Guid.NewGuid();
        var item = Item() with { ExperienceId = experience };
        var final = new TaskCompletionSource<TaskInventoryUploadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = new FakeTarget { Before = exists ? [item] : [], After = [item], UploadCompletion = final.Task };
        var operation = new TaskInventoryWriter(target).WriteAsync(Name, Request() with { ExpectedExperienceId = experience }, default);
        Assert.False(operation.IsCompleted);
        Assert.Equal(experience, target.PreflightExperience);
        Assert.Equal(experience, target.UploadExperience);
        Assert.Equal(exists, target.Existing is not null);
        final.SetResult(new(true, true, [], item.AssetId, item.ItemId));
        var result = await operation;
        Assert.True(result.Success);
        Assert.Equal(experience, result.Item!.ExperienceId);
        Assert.Equal(2, target.Reads);
    }

    [Theory]
    [InlineData("unknown", false)]
    [InlineData("none", false)]
    [InlineData("wrong", false)]
    [InlineData("unknown", true)]
    [InlineData("none", true)]
    [InlineData("wrong", true)]
    public async Task CompiledSourceDoesNotProveExperience(string association, bool exists)
    {
        var item = Item() with { ExperienceId = association switch { "none" => Guid.Empty, "wrong" => Guid.NewGuid(), _ => null } };
        var target = new FakeTarget { Before = exists ? [item] : [], After = [item],
            UploadCompletion = Task.FromResult(new TaskInventoryUploadResult(true, true, [], item.AssetId, item.ItemId)) };
        var result = await new TaskInventoryWriter(target).WriteAsync(Name, Request() with { ExpectedExperienceId = Guid.NewGuid() }, default);
        Assert.False(result.Success);
        Assert.True(result.UploadSucceeded);
        Assert.True(result.OutcomeUnknown);
        Assert.Equal(association == "unknown" ? "experience_unconfirmed" : "experience_mismatch", result.ErrorCode);
        Assert.Equal(association == "unknown", result.Retryable);
    }

    [Theory]
    [InlineData("experience_permission_denied")]
    [InlineData("experience_authority_unconfirmed")]
    [InlineData("capability_unavailable")]
    public async Task FailedExperiencePreflightPreventsAllInventoryWork(string code)
    {
        var target = new FakeTarget { PreflightFailure = new TaskInventoryException(code, "Cannot verify authority.") };
        var error = await Assert.ThrowsAsync<TaskInventoryException>(() => new TaskInventoryWriter(target).WriteAsync(
            Name, Request() with { ExpectedExperienceId = Guid.NewGuid() }, default));
        Assert.Equal(code, error.Code);
        Assert.False(error.OutcomeUnknown);
        Assert.Equal(0, target.Reads);
        Assert.Equal(0, target.Uploads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiredExperienceCompileFailureNeverReportsSuccess(bool exists)
    {
        var target = new FakeTarget { Before = exists ? [Item()] : [],
            UploadCompletion = Task.FromResult(new TaskInventoryUploadResult(true, false, ["compile denied"])) };
        var result = await new TaskInventoryWriter(target).WriteAsync(Name, Request() with { ExpectedExperienceId = Guid.NewGuid() }, default);
        Assert.False(result.Success);
        Assert.Equal("compile_failed", result.ErrorCode);
        Assert.Equal(1, target.Reads);
    }

    [Fact]
    public async Task SameNameSourceAndExperienceOnDifferentItemCannotSatisfyVerification()
    {
        var item = Item() with { ExperienceId = Guid.NewGuid() };
        var target = new FakeTarget { Before = [item], After = [item with { ItemId = Guid.NewGuid().ToString() }],
            UploadCompletion = Task.FromResult(new TaskInventoryUploadResult(true, true, [], item.AssetId, item.ItemId)) };
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TaskInventoryWriter(target).WriteAsync(
            Name, Request() with { ExpectedExperienceId = item.ExperienceId }, timeout.Token));
    }

    [Fact]
    public async Task OptionalExperienceDoesNotRequireMetadataOrAuthority()
    {
        var item = Item();
        var target = new FakeTarget { Before = [item], After = [item], PreflightFailure = new InvalidOperationException() };
        var result = await new TaskInventoryWriter(target).WriteAsync(Name, Request(), default);
        Assert.True(result.Success);
        Assert.Null(target.PreflightExperience);
        Assert.Null(target.UploadExperience);
    }

    [Theory]
    [InlineData("script", true)]
    [InlineData("notecard", false)]
    public async Task InvalidExperienceRequestCannotTouchInventory(string kind, bool zero)
    {
        var target = new FakeTarget();
        await Assert.ThrowsAsync<ArgumentException>(() => new TaskInventoryWriter(target).WriteAsync(
            Name, Request(kind) with { ExpectedExperienceId = zero ? Guid.Empty : Guid.NewGuid() }, default));
        Assert.Equal(0, target.Reads);
        Assert.Equal(0, target.Uploads);
        Assert.Null(target.PreflightExperience);
    }

    [Fact]
    public async Task OldAssetAndRunningCopyCannotSatisfyReadback()
    {
        var item = Item();
        var target = new FakeTarget { Before = [item], After = [item with { Running = true }],
            UploadCompletion = Task.FromResult(new TaskInventoryUploadResult(true, true, [], Guid.NewGuid().ToString())) };
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TaskInventoryWriter(target).WriteAsync(Name, Request(), timeout.Token));
    }

    [Fact]
    public async Task HashMismatchIsRejectedBeforeInventoryAccess()
    {
        var target = new FakeTarget();
        await Assert.ThrowsAsync<ArgumentException>(() => new TaskInventoryWriter(target).WriteAsync(Name, Request() with { ExpectedSha256 = "bad" }, default));
        Assert.Equal(0, target.Reads);
    }

    [Fact]
    public void NormalizationMatchesLfPackageText()
    {
        Assert.Equal(TaskInventoryContent.Hash(Encoding.UTF8.GetBytes("alpha\nbeta\n")),
            TaskInventoryContent.Hash(Encoding.UTF8.GetBytes("\uFEFFalpha\r\nbeta")));
    }

    [Fact]
    public async Task InventoryBodiesAndExceptionsAreNeverCapturedByRequestDiagnostics()
    {
        var logger = new CapturingLogger();
        var originalResponse = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/objects/00000000-0000-0000-0000-000000000001/inventory/items/payload.lsl";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("private source"));
        context.Request.ContentLength = context.Request.Body.Length;
        context.Request.ContentType = "application/json";
        context.Response.Body = originalResponse;
        var middleware = new RequestDiagnosticsMiddleware(c =>
        {
            Assert.Same(originalResponse, c.Response.Body);
            throw new InvalidOperationException("private compiler snippet");
        }, new BotConfig { Diagnostics = new() { LogApiCalls = true, LogApiBodies = true } }, logger);
        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));
        Assert.Empty(logger.Messages);
    }

    [Fact]
    public void TaskInventoryDoesNotAllowAnonymousDevelopmentAuthentication()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/objects/00000000-0000-0000-0000-000000000001/inventory/inspect";
        Assert.False(MunibotAuthentication.TryAuthenticate(context, new BotConfig(), out _));
    }

    [Fact]
    public void TextureScopeDoesNotAuthorizeTaskInventory()
    {
        var principal = new MunibotTokenPrincipal("texture-only", new HashSet<string> { AuthScopes.TextureUpload });
        Assert.False(MunibotAuthentication.HasAnyScope(principal, [AuthScopes.TaskInventoryWrite]));
    }

    private sealed class FakeTarget : ITaskInventoryTarget
    {
        public IReadOnlyList<TaskInventoryItemDto> Before = [];
        public IReadOnlyList<TaskInventoryItemDto> After = [];
        public Exception? ReadFailure;
        public Exception? ReadFailureAfterUpload;
        public Task<TaskInventoryUploadResult> UploadCompletion = Task.FromResult(new TaskInventoryUploadResult(true, true, []));
        public int Reads;
        public int Uploads;
        public TaskInventoryItemDto? Existing;
        public Guid? PreflightExperience;
        public Guid? UploadExperience;
        public Exception? PreflightFailure;
        public Task PreflightExperienceAsync(Guid experienceId, CancellationToken ct)
        {
            PreflightExperience = experienceId;
            return PreflightFailure is null ? Task.CompletedTask : Task.FromException(PreflightFailure);
        }
        public Task<IReadOnlyList<TaskInventoryItemDto>> InspectAsync(IReadOnlyList<string> names, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Reads++;
            var failure = Uploads > 0 ? ReadFailureAfterUpload ?? ReadFailure : ReadFailure;
            return failure is not null ? Task.FromException<IReadOnlyList<TaskInventoryItemDto>>(failure)
                : Task.FromResult(Uploads == 0 ? Before : After);
        }
        public Task<TaskInventoryUploadResult> UploadAsync(string name, string kind, byte[] source, TaskInventoryItemDto? existing,
            Guid? expectedExperienceId, CancellationToken ct)
        {
            Uploads++;
            Existing = existing;
            UploadExperience = expectedExperienceId;
            return UploadCompletion;
        }
    }

    private sealed class CapturingLogger : ILogger<RequestDiagnosticsMiddleware>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, ex));
    }
}

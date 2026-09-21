using System.Net;
using System.Text;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace Munibot.Tests;

public sealed class TaskInventoryAssetUploaderTests
{
    private static readonly Uri Cap = new("https://simulator.example.test/cap");
    private static (HttpResponseMessage response, byte[] data) Response(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        (new HttpResponseMessage(status), Encoding.UTF8.GetBytes(body));
    private const string Handshake = "{\"state\":\"upload\",\"uploader\":\"https://simulator.example.test/upload\"}";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FinalStageIsAwaitedForTaskAndTemporaryAgentUpload(bool task)
    {
        var final = new TaskCompletionSource<(HttpResponseMessage response, byte[] data)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var experience = Guid.NewGuid();
        var source = Encoding.UTF8.GetBytes("private source");
        var uploader = new TaskInventoryAssetUploader((uri, body, _) =>
        {
            Assert.Equal(Cap, uri);
            Assert.Equal(task, body.ContainsKey("experience"));
            Assert.Equal(task, body.ContainsKey("is_script_running"));
            return Task.FromResult(Response(Handshake));
        }, (uri, bytes, _) =>
        {
            Assert.Equal("/upload", uri.AbsolutePath);
            Assert.Same(source, bytes);
            return final.Task;
        });
        var operation = uploader.UploadAsync(Cap, TaskInventoryAssetCodec.UploadBody(UUID.Random(), task ? UUID.Random() : null, true, experience), source, true, default);
        Assert.False(operation.IsCompleted);
        var asset = Guid.NewGuid();
        final.SetResult(Response($"{{\"state\":\"complete\",\"compiled\":true,\"new_asset\":\"{asset}\"}}"));
        var result = await operation;
        Assert.True(result.Uploaded);
        Assert.True(result.Compiled);
        Assert.Equal(asset.ToString(), result.AssetId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PermissionRejectionDistinguishesHandshakeFromUncertainFinalUpload(bool finalStage)
    {
        var uploads = 0;
        var uploader = new TaskInventoryAssetUploader((_, _, _) => Task.FromResult(finalStage ? Response(Handshake) : Response("private error", HttpStatusCode.Forbidden)),
            (_, _, _) => { uploads++; return Task.FromResult(Response("private error", HttpStatusCode.Forbidden)); });
        var error = await Assert.ThrowsAsync<TaskInventoryException>(() => uploader.UploadAsync(Cap, new(), [], true, default));
        Assert.Equal(finalStage ? "upload_unconfirmed" : "upload_rejected", error.Code);
        Assert.Equal(finalStage, error.OutcomeUnknown);
        Assert.Equal(finalStage ? 1 : 0, uploads);
        Assert.DoesNotContain("private", error.Message);
    }

    [Theory]
    [InlineData("private malformed response")]
    [InlineData("{\"private response\":")]
    [InlineData("<?xml version=\"1.0\"?><llsd><map>private response")]
    public async Task MalformedFinalResponseIsSanitizedAndUncertain(string body)
    {
        var uploader = new TaskInventoryAssetUploader((_, _, _) => Task.FromResult(Response(Handshake)),
            (_, _, _) => Task.FromResult(Response(body)));
        var error = await Assert.ThrowsAsync<TaskInventoryException>(() => uploader.UploadAsync(Cap, new(), [], true, default));
        Assert.Equal("upload_unconfirmed", error.Code);
        Assert.True(error.OutcomeUnknown);
        Assert.DoesNotContain("private", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task CompilationFailureIsReadFromFinalResponse()
    {
        var uploader = new TaskInventoryAssetUploader((_, _, _) => Task.FromResult(Response(Handshake)),
            (_, _, _) => Task.FromResult(Response("{\"state\":\"complete\",\"compiled\":false,\"errors\":[\"compile failure\"]}")));
        var result = await uploader.UploadAsync(Cap, new(), [], true, default);
        Assert.True(result.Uploaded);
        Assert.False(result.Compiled);
        Assert.Equal("compile failure", Assert.Single(result.Messages));
    }
}

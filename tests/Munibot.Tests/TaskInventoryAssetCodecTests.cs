using System.Text;
using OpenMetaverse;
using OpenMetaverse.Assets;
using OpenMetaverse.StructuredData;

namespace Munibot.Tests;

public sealed class TaskInventoryAssetCodecTests
{
    [Fact]
    public void TaskScriptUploadIncludesExpectedExperienceAndKeepsScriptStopped()
    {
        var item = UUID.Random();
        var task = UUID.Random();
        var experience = Guid.NewGuid();
        var body = TaskInventoryAssetCodec.UploadBody(item, task, true, experience);
        Assert.Equal(item, body["item_id"].AsUUID());
        Assert.Equal(task, body["task_id"].AsUUID());
        Assert.Equal(new UUID(experience), body["experience"].AsUUID());
        Assert.Equal(OSDType.UUID, body["experience"].Type);
        Assert.False(body["is_script_running"].AsBoolean());
        Assert.Equal("mono", body["target"].AsString());
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void UnassociatedScriptsAgentUploadsAndNotecardsKeepTheirWireContract(bool task, bool script)
    {
        var body = TaskInventoryAssetCodec.UploadBody(UUID.Random(), task ? UUID.Random() : null, script, null);
        Assert.False(body.ContainsKey("experience"));
        Assert.Equal(script && task, body.ContainsKey("is_script_running"));
        Assert.Equal(task, body.ContainsKey("task_id"));
        Assert.Equal(script, body.ContainsKey("target"));
    }

    [Fact]
    public void TemporaryAgentScriptNeverGetsTheTaskExperienceField()
    {
        var body = TaskInventoryAssetCodec.UploadBody(UUID.Random(), null, true, Guid.NewGuid());
        Assert.False(body.ContainsKey("experience"));
        Assert.False(body.ContainsKey("is_script_running"));
    }

    [Fact]
    public void NotecardsUseLindenEncodingAndRoundTripUnicode()
    {
        const string source = "BundleId=sha256-test\nScript=café.lsl|1\n";
        var bytes = TaskInventoryAssetCodec.EncodeNotecard(Encoding.UTF8.GetBytes(source));
        Assert.StartsWith("Linden text version 2", Encoding.UTF8.GetString(bytes));
        var card = new AssetNotecard(UUID.Random(), bytes);
        Assert.True(card.Decode());
        Assert.Equal(source, card.BodyText);
    }

    [Fact]
    public void CompilerFailureWithoutAnAssetStillReturnsDiagnostics()
    {
        var result = TaskInventoryAssetCodec.ReadCompletion(OSDParser.DeserializeJson(
            "{\"state\":\"complete\",\"compiled\":false,\"errors\":[\"(3, 2): syntax error\"]}"), true);
        Assert.True(result.Uploaded);
        Assert.False(result.Compiled);
        Assert.Single(result.Messages);
        Assert.Null(result.AssetId);
    }

    [Theory]
    [InlineData("upload")]
    [InlineData("error")]
    public void HandshakeAndIntermediateResponsesAreNotCompletion(string state)
    {
        var error = Assert.Throws<TaskInventoryException>(() => TaskInventoryAssetCodec.ReadCompletion(
            new OSDMap { ["state"] = OSD.FromString(state) }, true));
        Assert.True(error.OutcomeUnknown);
    }

    [Fact]
    public void SuccessfulCompilationRequiresTheFinalAssetId()
    {
        var asset = UUID.Random();
        var result = TaskInventoryAssetCodec.ReadCompletion(new OSDMap
        {
            ["state"] = OSD.FromString("complete"), ["compiled"] = OSD.FromBoolean(true), ["new_asset"] = OSD.FromUUID(asset)
        }, true);
        Assert.True(result.Compiled);
        Assert.Equal(asset.ToString(), result.AssetId);
    }
}

using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace Munibot.Tests;

public sealed class TaskInventoryExperienceTests
{
    private static readonly Uri Cap = new("https://simulator.example.test/cap");

    [Theory]
    [InlineData("GetMetadata")]
    [InlineData("GetCreatorExperiences")]
    [InlineData("UpdateScriptTask")]
    public async Task MissingCapabilityFailsBeforeAuthorityRequest(string missing)
    {
        var experience = new TaskInventoryExperience(name => name == missing ? null : Cap,
            (_, _) => throw new InvalidOperationException("Must not send a request."),
            (_, _, _) => throw new InvalidOperationException());
        var error = await Assert.ThrowsAsync<TaskInventoryException>(() => experience.PreflightAsync(Guid.NewGuid(), default));
        Assert.Equal("capability_unavailable", error.Code);
        Assert.False(error.OutcomeUnknown);
    }

    [Fact]
    public async Task CreatorAuthorityMustIncludeExpectedExperience()
    {
        var expected = UUID.Random();
        var experience = Create(new OSDMap { ["experience_ids"] = new OSDArray { OSD.FromUUID(expected) } });
        await experience.PreflightAsync(Guid.Parse(expected.ToString()), default);
        var error = await Assert.ThrowsAsync<TaskInventoryException>(() => experience.PreflightAsync(Guid.NewGuid(), default));
        Assert.Equal("experience_permission_denied", error.Code);
        Assert.False(error.OutcomeUnknown);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"experience_ids\":null}")]
    [InlineData("{\"experience_ids\":[\"malformed\"]}")]
    [InlineData("{\"error\":\"denied\",\"experience_ids\":[]}")]
    public async Task InvalidAuthorityIsNotPermissionProof(string json)
    {
        var error = await Assert.ThrowsAsync<TaskInventoryException>(() =>
            Create(OSDParser.DeserializeJson(json)).PreflightAsync(Guid.NewGuid(), default));
        Assert.Equal("experience_authority_unconfirmed", error.Code);
    }

    [Fact]
    public async Task HttpDenialCannotProveAuthorityOrAssociation()
    {
        var experience = new TaskInventoryExperience(_ => Cap,
            (_, _) => throw new HttpRequestException("private response"),
            (_, _, _) => throw new HttpRequestException("private response"));
        var error = await Assert.ThrowsAsync<TaskInventoryException>(() => experience.PreflightAsync(Guid.NewGuid(), default));
        Assert.Equal("experience_authority_unconfirmed", error.Code);
        Assert.DoesNotContain("private", error.Message);
        Assert.Null(await experience.ReadAsync(UUID.Random(), UUID.Random(), default));
    }

    [Fact]
    public async Task MetadataRequestUsesExactObjectAndTaskItemOnEveryRead()
    {
        var objectId = UUID.Random();
        var itemId = UUID.Random();
        var expected = UUID.Random();
        var reads = 0;
        var experience = new TaskInventoryExperience(_ => Cap, (_, _) => throw new InvalidOperationException(),
            (uri, body, _) =>
            {
                Assert.Equal(Cap, uri);
                Assert.Equal(objectId, body["object-id"].AsUUID());
                Assert.Equal(itemId, body["item-id"].AsUUID());
                Assert.Equal("experience", Assert.Single(Assert.IsType<OSDArray>(body["fields"])).AsString());
                reads++;
                return Task.FromResult<OSD>(new OSDMap { ["experience"] = OSD.FromUUID(reads == 1 ? expected : UUID.Zero) });
            });
        Assert.Equal(Guid.Parse(expected.ToString()), await experience.ReadAsync(objectId, itemId, default));
        Assert.Equal(Guid.Empty, await experience.ReadAsync(objectId, itemId, default));
        Assert.Equal(2, reads);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"experience\":null}")]
    [InlineData("{\"experience\":false}")]
    [InlineData("{\"experience\":\"invalid\"}")]
    [InlineData("{\"error\":\"denied\",\"experience\":\"00000000-0000-0000-0000-000000000000\"}")]
    public async Task MissingMalformedOrRejectedMetadataIsUnknownNotZero(string json)
    {
        Assert.Null(await Create(OSDParser.DeserializeJson(json)).ReadAsync(UUID.Random(), UUID.Random(), default));
    }

    [Fact]
    public async Task MissingMetadataCapabilityLeavesInspectionUnknown()
    {
        var experience = new TaskInventoryExperience(_ => null, (_, _) => throw new InvalidOperationException(),
            (_, _, _) => throw new InvalidOperationException());
        Assert.Null(await experience.ReadAsync(UUID.Random(), UUID.Random(), default));
    }

    [Theory]
    [InlineData("<llsd><map><key>experience</key><uuid>not-a-uuid</uuid></map></llsd>", false)]
    [InlineData("<llsd><map><key>experience</key><uuid /></map></llsd>", true)]
    [InlineData("<llsd><map><key>experience</key><uuid>00000000-0000-0000-0000-000000000000</uuid></map></llsd>", true)]
    public async Task XmlMetadataDistinguishesInvalidUuidFromExplicitZero(string xml, bool zero)
    {
        var response = TaskInventoryAssetCodec.ReadResponse(System.Text.Encoding.UTF8.GetBytes(xml));
        var result = await Create(response).ReadAsync(UUID.Random(), UUID.Random(), default);
        Assert.Equal(zero ? Guid.Empty : (Guid?)null, result);
    }

    [Fact]
    public async Task CallerCancellationIsNotSwallowedAsUnknownMetadata()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var experience = new TaskInventoryExperience(_ => Cap, (_, _) => throw new InvalidOperationException(),
            (_, _, ct) => Task.FromCanceled<OSD>(ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => experience.ReadAsync(UUID.Random(), UUID.Random(), cancellation.Token));
    }

    private static TaskInventoryExperience Create(OSD response) => new(_ => Cap,
        (_, _) => Task.FromResult(response), (_, _, _) => Task.FromResult(response));
}

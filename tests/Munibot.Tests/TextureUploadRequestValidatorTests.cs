using Munibot;

namespace Munibot.Tests;

public sealed class TextureUploadRequestValidatorTests
{
    [Fact]
    public void NormalizeName_TrimsTextureName()
    {
        var name = TextureUploadRequestValidator.NormalizeName(" Example Crest ");

        Assert.Equal("Example Crest", name);
    }

    [Fact]
    public void NormalizeDescription_DefaultsMissingDescriptionToEmpty()
    {
        var description = TextureUploadRequestValidator.NormalizeDescription(" ");

        Assert.Equal(string.Empty, description);
    }

    [Fact]
    public void DecodeTextureData_DecodesBase64()
    {
        var data = TextureUploadRequestValidator.DecodeTextureData(Convert.ToBase64String([1, 2, 3]));

        Assert.Equal([1, 2, 3], data);
    }

    [Fact]
    public void DecodeTextureData_DecodesDataUri()
    {
        var base64 = Convert.ToBase64String([4, 5, 6]);
        var data = TextureUploadRequestValidator.DecodeTextureData($"data:image/jp2;base64,{base64}");

        Assert.Equal([4, 5, 6], data);
    }

    [Fact]
    public void DecodeTextureData_RejectsInvalidBase64()
    {
        Assert.Throws<ArgumentException>(() => TextureUploadRequestValidator.DecodeTextureData("not valid!"));
    }

    [Fact]
    public void RequireUploadFeeConfirmation_RequiresExplicitConfirmation()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            TextureUploadRequestValidator.RequireUploadFeeConfirmation(false));

        Assert.Contains("confirmUploadFee", ex.Message);
    }

    [Fact]
    public void TryParse_ReadsSecondLifeMismatchResponse()
    {
        const string rawResult = """
            {
              "message": "The server expects a different upload fee",
              "state": "failure",
              "error": {
                "message": "The server expects a different upload fee",
                "identifier": "Upload_UploadPriceDiffers",
                "upload_price": 0,
                "expected_upload_price": 10
              }
            }
            """;

        var mismatch = TextureUploadCostMismatch.TryParse(rawResult);

        Assert.NotNull(mismatch);
        Assert.Equal(0, mismatch.UploadPrice);
        Assert.Equal(10, mismatch.ExpectedUploadPrice);
    }

    [Fact]
    public void TryParse_IgnoresUnrelatedFailure()
    {
        var mismatch = TextureUploadCostMismatch.TryParse(
            """{"state":"failure","error":{"identifier":"Other"}}""");

        Assert.Null(mismatch);
    }
}

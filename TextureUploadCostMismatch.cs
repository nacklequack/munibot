using System.Text.Json;

namespace Munibot;

public static class TextureUploadCostMismatch
{
    public const string Identifier = "Upload_UploadPriceDiffers";

    public static TextureUploadCostMismatchResult? TryParse(string? rawResult)
    {
        if (string.IsNullOrWhiteSpace(rawResult) ||
            rawResult.IndexOf(Identifier, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(rawResult);
            var root = document.RootElement;

            int? uploadPrice = null;
            int? expectedUploadPrice = null;

            if (TryGetInt32(root, "upload_price", out var rootUploadPrice))
            {
                uploadPrice = rootUploadPrice;
            }

            if (root.TryGetProperty("error", out var error) &&
                TryGetInt32(error, "upload_price", out var errorUploadPrice))
            {
                uploadPrice = errorUploadPrice;
            }

            if (TryGetInt32(root, "expected_upload_price", out var rootExpectedUploadPrice))
            {
                expectedUploadPrice = rootExpectedUploadPrice;
            }

            if (root.TryGetProperty("error", out error) &&
                TryGetInt32(error, "expected_upload_price", out var errorExpectedUploadPrice))
            {
                expectedUploadPrice = errorExpectedUploadPrice;
            }

            if (uploadPrice is null && expectedUploadPrice is null)
            {
                return null;
            }

            return new TextureUploadCostMismatchResult(uploadPrice, expectedUploadPrice);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetInt32(JsonElement element, string propertyName, out int value)
    {
        value = 0;

        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number)
        {
            return property.TryGetInt32(out value);
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            return int.TryParse(property.GetString(), out value);
        }

        return false;
    }
}

public sealed record TextureUploadCostMismatchResult(
    int? UploadPrice,
    int? ExpectedUploadPrice);

using System.Text.Json;

namespace Munibot;

public static class TextureUploadCostMismatch
{
    public const string Identifier = "Upload_UploadPriceDiffers";

    public static int? TryGetExpectedUploadPrice(string? rawResult)
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

            if (TryGetInt32(root, "expected_upload_price", out var rootPrice))
            {
                return rootPrice;
            }

            if (root.TryGetProperty("error", out var error) &&
                TryGetInt32(error, "expected_upload_price", out var errorPrice))
            {
                return errorPrice;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
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

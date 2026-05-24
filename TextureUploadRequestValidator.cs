using System.Buffers.Text;

namespace Munibot;

public static class TextureUploadRequestValidator
{
    public const int MaxTextureNameLength = 63;
    public const int MaxTextureDescriptionLength = 127;
    public const int MaxTextureBytes = 20 * 1024 * 1024;
    public const int ExpectedTextureUploadCostLinden = 10;

    public static string NormalizeName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new ArgumentException("Texture name is required.");
        }

        if (trimmed.Length > MaxTextureNameLength)
        {
            throw new ArgumentException($"Texture name must be {MaxTextureNameLength} characters or fewer.");
        }

        return trimmed;
    }

    public static string NormalizeDescription(string? description)
    {
        var trimmed = description?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        if (trimmed.Length > MaxTextureDescriptionLength)
        {
            throw new ArgumentException(
                $"Texture description must be {MaxTextureDescriptionLength} characters or fewer.");
        }

        return trimmed;
    }

    public static byte[] DecodeTextureData(string? textureDataBase64)
    {
        var trimmed = textureDataBase64?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new ArgumentException("Base64 texture data is required.");
        }

        var commaIndex = trimmed.IndexOf(',');
        if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && commaIndex >= 0)
        {
            trimmed = trimmed[(commaIndex + 1)..].Trim();
        }

        if (trimmed.Length > Base64.GetMaxEncodedToUtf8Length(MaxTextureBytes))
        {
            throw new ArgumentException($"Texture upload data must decode to {MaxTextureBytes} bytes or fewer.");
        }

        try
        {
            var data = Convert.FromBase64String(trimmed);
            if (data.Length == 0)
            {
                throw new ArgumentException("Texture upload data cannot be empty.");
            }

            if (data.Length > MaxTextureBytes)
            {
                throw new ArgumentException($"Texture upload data must be {MaxTextureBytes} bytes or fewer.");
            }

            return data;
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("Texture upload data must be valid base64.", ex);
        }
    }

    public static void RequireUploadFeeConfirmation(bool? confirmUploadFee)
    {
        if (confirmUploadFee != true)
        {
            throw new ArgumentException(
                "Texture uploads charge the bot account's Second Life upload fee; confirmUploadFee must be true.");
        }
    }
}

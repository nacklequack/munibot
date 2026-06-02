using OpenMetaverse;

namespace Munibot;

public static class WalletRequestValidator
{
    public const int MaxPaymentDescriptionLength = 127;
    public const int MaxPaymentAmountLinden = 1_000_000;

    public static UUID NormalizeAvatarId(string? avatarId)
    {
        var trimmed = avatarId?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) ||
            !UUID.TryParse(trimmed, out var parsed) ||
            parsed == UUID.Zero)
        {
            throw new ArgumentException("A valid Second Life avatar UUID is required.");
        }

        return parsed;
    }

    public static int NormalizeAmount(int? amount)
    {
        if (!amount.HasValue || amount.Value <= 0)
        {
            throw new ArgumentException("Payment amount must be greater than zero.");
        }

        if (amount.Value > MaxPaymentAmountLinden)
        {
            throw new ArgumentException($"Payment amount must be L${MaxPaymentAmountLinden} or less.");
        }

        return amount.Value;
    }

    public static string NormalizeDescription(string? description)
    {
        var trimmed = description?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        if (trimmed.Length > MaxPaymentDescriptionLength)
        {
            throw new ArgumentException(
                $"Payment description must be {MaxPaymentDescriptionLength} characters or fewer.");
        }

        return trimmed;
    }

    public static void RequirePaymentConfirmation(bool? confirmPayment)
    {
        if (confirmPayment != true)
        {
            throw new ArgumentException(
                "Outgoing Linden payments spend from the bot account; confirmPayment must be true.");
        }
    }
}

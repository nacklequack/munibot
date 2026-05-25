using System.Reflection;
using OpenMetaverse;

namespace Munibot;

public static class WalletEventMapper
{
    public static WalletEventDto? FromMoneyBalanceReply(MoneyBalanceReplyEventArgs reply, UUID botAgentId)
    {
        if (reply.TransactionID == UUID.Zero)
        {
            return null;
        }

        return FromTransactionDetails(
            reply.Success,
            reply.Balance,
            reply.TransactionID.ToString(),
            reply.TransactionInfo,
            reply.Description,
            botAgentId,
            DateTimeOffset.UtcNow);
    }

    public static WalletEventDto? FromTransactionDetails(
        bool success,
        int balance,
        string transactionId,
        object? transactionInfo,
        string? description,
        UUID botAgentId,
        DateTimeOffset occurredAtUtc)
    {
        if (string.IsNullOrWhiteSpace(transactionId) ||
            string.Equals(transactionId, UUID.Zero.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var info = transactionInfo;
        var amount = ReadInt32(
            info,
            "Amount",
            "TransactionAmount",
            "AmountTransferred",
            "TransactionAmountLinden");
        var source = ReadUuidString(info, "SourceID", "SourceId", "SourceUUID", "SourceUuid", "Source");
        var target = ReadUuidString(
            info,
            "DestID",
            "DestId",
            "DestinationID",
            "DestinationId",
            "TargetID",
            "TargetId",
            "Target",
            "Destination");

        if (amount is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(source) && string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        source ??= botAgentId.ToString();
        target ??= botAgentId.ToString();

        return new WalletEventDto(
            success,
            balance,
            Math.Abs(amount.Value),
            transactionId.Trim(),
            source,
            target,
            ReadString(info, "TransactionType", "Type", "MoneyTransactionType"),
            string.IsNullOrWhiteSpace(description)
                ? ReadString(info, "Description", "ItemDescription")
                : description,
            occurredAtUtc);
    }

    public static WalletEventDto FromOutgoingPaymentResult(
        MoneyBalanceReplyEventArgs reply,
        UUID botAgentId,
        UUID targetAvatarId,
        int amount,
        string description,
        DateTimeOffset occurredAtUtc)
        => new(
            reply.Success,
            reply.Balance,
            amount,
            reply.TransactionID == UUID.Zero ? Guid.NewGuid().ToString("D") : reply.TransactionID.ToString(),
            botAgentId.ToString(),
            targetAvatarId.ToString(),
            "Gift",
            string.IsNullOrWhiteSpace(reply.Description) ? description : reply.Description,
            occurredAtUtc);

    private static string? ReadUuidString(object? source, params string[] names)
    {
        var value = ReadMemberValue(source, names);
        if (value is null)
        {
            return null;
        }

        var text = Convert.ToString(value)?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return UUID.TryParse(text, out var uuid) && uuid != UUID.Zero
            ? uuid.ToString()
            : text;
    }

    private static string? ReadString(object? source, params string[] names)
    {
        var value = ReadMemberValue(source, names);
        var text = Convert.ToString(value)?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static int? ReadInt32(object? source, params string[] names)
    {
        var value = ReadMemberValue(source, names);
        if (value is null)
        {
            return null;
        }

        if (value is int number)
        {
            return number;
        }

        return int.TryParse(Convert.ToString(value), out var parsed) ? parsed : null;
    }

    private static object? ReadMemberValue(object? source, params string[] names)
    {
        if (source is null)
        {
            return null;
        }

        var type = source.GetType();
        foreach (var name in names)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase;
            var property = type.GetProperty(name, flags);
            if (property is not null && property.GetIndexParameters().Length == 0)
            {
                return property.GetValue(source);
            }

            var field = type.GetField(name, flags);
            if (field is not null)
            {
                return field.GetValue(source);
            }
        }

        return null;
    }
}

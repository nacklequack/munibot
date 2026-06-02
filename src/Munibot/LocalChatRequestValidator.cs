using OpenMetaverse;

namespace Munibot;

public static class LocalChatRequestValidator
{
    public const int MaxMessageLength = AgentManager.MaxChatMessageSize;

    public static string NormalizeMessage(string? message)
    {
        var trimmed = message?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new ArgumentException("Local chat message text is required.");
        }

        if (trimmed.Length > MaxMessageLength)
        {
            throw new ArgumentException($"Local chat message text must be {MaxMessageLength} characters or fewer.");
        }

        return trimmed;
    }

    public static int NormalizeChannel(int? channel) => channel ?? 0;

    public static ChatType NormalizeChatType(string? chatType)
    {
        if (string.IsNullOrWhiteSpace(chatType))
        {
            return ChatType.Normal;
        }

        return chatType.Trim().ToLowerInvariant() switch
        {
            "normal" or "say" => ChatType.Normal,
            "whisper" => ChatType.Whisper,
            "shout" => ChatType.Shout,
            _ => throw new ArgumentException("Local chat type must be one of: normal, whisper, shout.")
        };
    }
}

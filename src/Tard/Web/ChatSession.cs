namespace Tard.Web;

public class ChatSession
{
    public required string Id { get; init; }
    public string Title { get; set; } = "New Chat";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastMessageAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Persisted copy of <c>Messages.Count</c>, written on every save so the sidebar listing can be
    /// built without deserializing every message of every chat. Null on files written before this
    /// field existed — <see cref="JsonFileChatStore"/> falls back to a full read for those.
    /// </summary>
    public int? MessageCount { get; set; }

    public List<WebChatMessage> Messages { get; init; } = new();
}

/// <summary>
/// The subset of a chat the sidebar needs. Deserializing into this instead of
/// <see cref="ChatSession"/> lets System.Text.Json skip the message array without allocating a
/// <see cref="WebChatMessage"/> for every turn ever sent.
/// </summary>
public class ChatSessionSummary
{
    public required string Id { get; init; }
    public string Title { get; set; } = "New Chat";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastMessageAt { get; set; }
    public int MessageCount { get; set; }
}

public class WebChatMessage
{
    public required string Role { get; init; }
    public required string Text { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

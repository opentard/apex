namespace Tard.Messaging;

public record ChatMessage
{
    public required string MessageId { get; init; }
    public required string FromPhoneNumber { get; init; }
    public required string SenderName { get; init; }
    public required string MessageType { get; init; }
    public string? TextBody { get; init; }
    public string? MediaId { get; init; }
    public string? GroupId { get; init; }
    public required DateTimeOffset ReceivedAt { get; init; }

    /// <summary>
    /// Which memory namespace this message reads and writes. Defaults to the sender.
    /// <para>
    /// The dashboard mints a fresh sender per chat ("web:{chatId}") so each chat keeps its own
    /// conversation history — but that also partitioned <em>memories</em> per chat, so "remember my
    /// name" in one chat was invisible in the next. Web chats therefore share one memory scope
    /// while keeping separate histories.
    /// </para>
    /// </summary>
    public string? MemoryScope { get; init; }
}

using Tard.Messaging;

namespace Tard.Agent;

public interface ITardAgent
{
    Task<string> ProcessMessageAsync(ChatMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Seeds a conversation's in-memory history from a durable transcript, but only when the agent
    /// has nothing for that sender yet.
    /// <para>
    /// Conversation state lives in process while web chats are persisted to disk, so after a
    /// restart the dashboard shows a full transcript to a user whose agent remembers none of it —
    /// the reply reads as amnesia. The dashboard replays the stored turns before the first message
    /// of a resumed chat to close that gap.
    /// </para>
    /// </summary>
    void SeedHistoryIfEmpty(string userId, IEnumerable<(string Role, string Text)> turns);
}

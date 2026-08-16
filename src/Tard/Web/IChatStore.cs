namespace Tard.Web;

public interface IChatStore
{
    Task<ChatSession> CreateAsync(CancellationToken ct = default);

    /// <summary>Sidebar listing. Returns summaries — never the full message history.</summary>
    Task<IReadOnlyList<ChatSessionSummary>> ListAsync(CancellationToken ct = default);

    Task<ChatSession?> GetAsync(string chatId, CancellationToken ct = default);
    Task SaveAsync(ChatSession session, CancellationToken ct = default);
    Task<bool> DeleteAsync(string chatId, CancellationToken ct = default);

    /// <summary>
    /// Applies <paramref name="mutate"/> to a stored chat under the store's lock and persists the
    /// result, so a read-modify-write cannot interleave with a concurrent one and lose messages.
    /// Returns null when the chat does not exist.
    /// </summary>
    Task<ChatSession?> UpdateAsync(string chatId, Action<ChatSession> mutate, CancellationToken ct = default);
}

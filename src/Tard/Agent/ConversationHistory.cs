using Tard.Ai;

namespace Tard.Agent;

/// <summary>
/// A single sender's conversation. Instances are reached through a singleton agent that the
/// polling worker and the dashboard's request threads both drive, so every access is guarded —
/// an unsynchronized List here can tear or throw mid-enumeration when a WhatsApp message and a
/// web message for the same sender overlap.
/// </summary>
public class ConversationHistory
{
    private readonly List<AiMessage> _messages = new();
    private readonly object _gate = new();
    private readonly int _maxMessages;

    public ConversationHistory(int maxMessages = 50)
    {
        _maxMessages = maxMessages;
    }

    /// <summary>A snapshot — the underlying list keeps changing as the tool loop runs.</summary>
    public IReadOnlyList<AiMessage> Messages
    {
        get
        {
            lock (_gate)
                return _messages.ToList();
        }
    }

    public void Add(AiMessage message)
    {
        lock (_gate)
        {
            _messages.Add(message);
            Trim();
        }
    }

    /// <summary>
    /// Drops the oldest messages while keeping the conversation valid for the Messages API.
    /// <para>
    /// The API rejects a conversation whose first message is not a <c>user</c> turn, and rejects a
    /// <c>tool_result</c> block with no matching <c>tool_use</c> in the message before it. A tool
    /// round is user / assistant(tool_use) / user(tool_result), so counting messages off the front
    /// can strand a tool_result at the head and 400 every subsequent request.
    /// </para>
    /// <para>
    /// So trimming moves in whole exchanges: it only ever cuts forward to the next message that is
    /// a legal conversation start. While a long tool loop is mid-flight there may be no such point,
    /// in which case the history is briefly allowed over its limit — the next ordinary user message
    /// becomes a valid cut point and the backlog is dropped then. Staying slightly over budget
    /// beats sending a request the API is guaranteed to reject.
    /// </para>
    /// </summary>
    private void Trim()
    {
        while (_messages.Count > _maxMessages)
        {
            var next = NextValidHead(1);
            if (next < 0)
                break;

            _messages.RemoveRange(0, next);
        }

        // Repair an invalid head, but never empty a non-empty history to do it.
        if (_messages.Count > 0 && !IsValidHead(_messages[0]))
        {
            var next = NextValidHead(1);
            if (next > 0)
                _messages.RemoveRange(0, next);
        }
    }

    private int NextValidHead(int from)
    {
        for (var i = from; i < _messages.Count; i++)
        {
            if (IsValidHead(_messages[i]))
                return i;
        }

        return -1;
    }

    /// <summary>A conversation may only begin with a user turn that is not a dangling tool result.</summary>
    private static bool IsValidHead(AiMessage message) =>
        message.Role == "user" && !message.Content.Any(c => c.Type == "tool_result");
}

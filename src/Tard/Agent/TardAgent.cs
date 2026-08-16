using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tard.Ai;
using Tard.Configuration;
using Tard.Memory;
using Tard.Messaging;
using Tard.Skills;

namespace Tard.Agent;

public class TardAgent : ITardAgent
{
    private readonly IAiClient _aiClient;
    private readonly SkillRegistry _skillRegistry;
    private readonly IMemoryStore _memoryStore;
    private readonly TardOptions _options;
    private readonly ILogger<TardAgent> _logger;
    private readonly ConcurrentDictionary<string, ConversationHistory> _histories = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastUsed = new();

    /// <summary>
    /// A single message can add up to 1 user turn + 2 messages per tool round. If the configured
    /// history limit were smaller than that, trimming could discard the whole conversation
    /// mid-loop and leave an empty messages array, which the Messages API rejects.
    /// </summary>
    private int HistoryCapacity => Math.Max(_options.MaxHistoryPerUser, (_options.MaxToolRounds * 2) + 2);

    private const string ToolLimitMessage =
        "I hit my limit on tool calls for this request, so I stopped there. " +
        "Please try again with a narrower or simpler request.";

    public TardAgent(
        IAiClient aiClient,
        SkillRegistry skillRegistry,
        IMemoryStore memoryStore,
        IOptions<TardOptions> options,
        ILogger<TardAgent> logger)
    {
        _aiClient = aiClient;
        _skillRegistry = skillRegistry;
        _memoryStore = memoryStore;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> ProcessMessageAsync(ChatMessage message, CancellationToken cancellationToken = default)
    {
        var userId = message.FromPhoneNumber;

        // Conversation history is per-sender; memories may be shared more widely (see MemoryScope).
        var memoryScope = string.IsNullOrWhiteSpace(message.MemoryScope) ? userId : message.MemoryScope;

        var history = _histories.GetOrAdd(userId, _ => new ConversationHistory(HistoryCapacity));
        _lastUsed[userId] = DateTimeOffset.UtcNow;
        EvictColdConversations();

        // Build system prompt with user memories
        var systemPrompt = await BuildSystemPromptAsync(memoryScope, userId, message.SenderName, cancellationToken);

        // Determine user content
        var userText = message.TextBody ?? $"[{message.MessageType} message received]";

        // Add user message to history
        history.Add(new AiMessage("user", new[] { new AiContentBlock { Type = "text", Text = userText } }));

        var tools = _skillRegistry.ToAiTools();
        var context = new SkillContext(memoryScope);

        // Tool-use loop
        for (int round = 0; round < _options.MaxToolRounds; round++)
        {
            var response = await _aiClient.ChatAsync(systemPrompt, history.Messages, tools, cancellationToken);

            // Add assistant response to history
            history.Add(new AiMessage("assistant", response.Content));

            if (response.StopReason != "tool_use")
            {
                // Extract text from response
                var text = string.Join("", response.Content
                    .Where(c => c.Type == "text" && c.Text is not null)
                    .Select(c => c.Text));

                return string.IsNullOrWhiteSpace(text) ? "I processed your request." : text;
            }

            // Execute tool calls
            var toolResults = new List<AiContentBlock>();
            foreach (var block in response.Content.Where(c => c.Type == "tool_use"))
            {
                var skill = _skillRegistry.GetSkill(block.ToolName!);
                string result;
                if (skill is null)
                {
                    result = JsonSerializer.Serialize(new { error = $"Unknown tool: {block.ToolName}" });
                }
                else
                {
                    try
                    {
                        _logger.LogInformation("Executing skill {Skill} for user {User}", block.ToolName, userId);
                        result = await skill.ExecuteAsync(block.ToolInput ?? default, context, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Skill {Skill} failed", block.ToolName);
                        result = JsonSerializer.Serialize(new { error = ex.Message });
                    }
                }

                toolResults.Add(new AiContentBlock
                {
                    Type = "tool_result",
                    ToolUseId = block.ToolUseId,
                    ToolResultContent = result
                });
            }

            // Add tool results to history as a user message
            history.Add(new AiMessage("user", toolResults));
        }

        // The loop ran out of rounds with the last turn still being tool results. Close the
        // conversation off with an assistant turn: without it the next incoming message would
        // append a second consecutive "user" message, which the Messages API rejects, wedging
        // this user's conversation permanently.
        _logger.LogWarning("Tool loop hit the {Max}-round limit for user {User}", _options.MaxToolRounds, userId);
        history.Add(new AiMessage("assistant", new[] { new AiContentBlock { Type = "text", Text = ToolLimitMessage } }));
        return ToolLimitMessage;
    }

    /// <inheritdoc />
    public void SeedHistoryIfEmpty(string userId, IEnumerable<(string Role, string Text)> turns)
    {
        if (string.IsNullOrEmpty(userId))
            return;

        // Only seed a conversation the agent has never seen. A live conversation already holds the
        // authoritative history (including tool rounds the transcript does not record).
        if (_histories.ContainsKey(userId))
            return;

        var history = new ConversationHistory(HistoryCapacity);
        foreach (var (role, text) in turns)
        {
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var normalized = role == "assistant" ? "assistant" : "user";
            history.Add(new AiMessage(normalized, new[] { new AiContentBlock { Type = "text", Text = text } }));
        }

        if (history.Messages.Count == 0)
            return;

        if (_histories.TryAdd(userId, history))
        {
            _lastUsed[userId] = DateTimeOffset.UtcNow;
            _logger.LogDebug("Seeded {Count} stored turns for {UserId}", history.Messages.Count, userId);
        }
    }

    /// <summary>
    /// Conversations are keyed by sender, and the web dashboard mints a fresh synthetic sender
    /// ("web:{chatId}") for every new chat — so without eviction the map grows for the lifetime of
    /// the process. Drop the least recently used conversations once past the configured cap.
    /// </summary>
    private void EvictColdConversations()
    {
        var limit = _options.MaxTrackedConversations;
        if (limit <= 0 || _histories.Count <= limit)
            return;

        foreach (var userId in _lastUsed.OrderBy(kv => kv.Value)
                                        .Take(_histories.Count - limit)
                                        .Select(kv => kv.Key)
                                        .ToList())
        {
            _histories.TryRemove(userId, out _);
            _lastUsed.TryRemove(userId, out _);
            _logger.LogDebug("Evicted cold conversation for {UserId}", userId);
        }
    }

    private async Task<string> BuildSystemPromptAsync(
        string memoryScope,
        string userId,
        string senderName,
        CancellationToken cancellationToken)
    {
        var basePrompt = _options.SystemPrompt;

        // Append user memories if any
        try
        {
            var memories = await _memoryStore.ListAsync(memoryScope, cancellationToken);
            if (memories.Count > 0)
            {
                var memoryText = string.Join("\n", memories.Select(m => $"- {m.Key}: {m.Value}"));
                basePrompt += $"\n\nYou are talking to {senderName} (phone: {userId}).\nTheir saved memories:\n{memoryText}";
            }
            else
            {
                basePrompt += $"\n\nYou are talking to {senderName} (phone: {userId}).";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load memories for scope {Scope}", memoryScope);
            basePrompt += $"\n\nYou are talking to {senderName} (phone: {userId}).";
        }

        return basePrompt;
    }
}

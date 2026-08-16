namespace Tard.Configuration;

public class TardOptions
{
    public const string SectionName = "Tard";

    public string OtWapUrl { get; set; } = "http://ot-wap:8080";
    public string AnthropicApiKey { get; set; } = "";
    /// <summary>
    /// Claude model id. The previous default, claude-sonnet-4-20250514, is deprecated and
    /// scheduled for retirement; claude-sonnet-5 is its successor in the same tier.
    /// </summary>
    public string AnthropicModel { get; set; } = "claude-sonnet-5";

    /// <summary>
    /// Output token ceiling per reply. This bounds thinking *and* response text together, and
    /// current models think by default — a tight value truncates the answer mid-sentence.
    /// </summary>
    public int MaxTokens { get; set; } = 16_000;
    public int PollingIntervalMs { get; set; } = 3000;
    public int MaxHistoryPerUser { get; set; } = 50;

    /// <summary>Maximum tool-execution rounds per message before the agent gives up and replies.</summary>
    public int MaxToolRounds { get; set; } = 10;

    /// <summary>
    /// Maximum number of conversations kept in memory at once. The least recently used
    /// conversation is evicted past this point so long-running instances do not grow without bound.
    /// </summary>
    public int MaxTrackedConversations { get; set; } = 500;

    /// <summary>
    /// Phone numbers (E.164) allowed to talk to the agent. Empty means every sender is allowed,
    /// which — because the agent can run shell commands — grants host command execution to anyone
    /// who knows the linked WhatsApp number. Set this in any real deployment.
    /// </summary>
    public string AllowedSenders { get; set; } = "";

    /// <summary>Enables the shell skill. Off by default: it executes arbitrary commands on the host.</summary>
    public bool EnableShellSkill { get; set; } = false;

    public string MemoryStorePath { get; set; } = "/data/memory";
    public string SystemPrompt { get; set; } =
        "You are tard, a helpful personal AI assistant. You communicate via WhatsApp. " +
        "Be concise and helpful. You can check the time and remember things for the user. " +
        "To remember something, call the memory tool with action='save' plus a key and value. " +
        "To look something up, call the memory tool with action='recall' and the key. " +
        "Use action='list' to see everything saved and action='delete' to remove a key.";
}

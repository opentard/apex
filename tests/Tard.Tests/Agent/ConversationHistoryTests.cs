using Tard.Agent;
using Tard.Ai;

namespace Tard.Tests.Agent;

public class ConversationHistoryTests
{
    private static AiMessage Text(string role, string text) =>
        new(role, new[] { new AiContentBlock { Type = "text", Text = text } });

    private static AiMessage ToolUse(string id) =>
        new("assistant", new[] { new AiContentBlock { Type = "tool_use", ToolUseId = id, ToolName = "t" } });

    private static AiMessage ToolResult(string id) =>
        new("user", new[] { new AiContentBlock { Type = "tool_result", ToolUseId = id, ToolResultContent = "{}" } });

    [Fact]
    public void Add_TracksMessages()
    {
        var history = new ConversationHistory(10);
        history.Add(Text("user", "Hi"));

        Assert.Single(history.Messages);
    }

    [Fact]
    public void Add_TrimsWhenOverLimit()
    {
        var history = new ConversationHistory(4);

        for (int i = 0; i < 6; i++)
            history.Add(Text(i % 2 == 0 ? "user" : "assistant", $"msg {i}"));

        Assert.True(history.Messages.Count <= 4);
    }

    [Fact]
    public void Messages_MostRecentKept()
    {
        var history = new ConversationHistory(2);

        history.Add(Text("user", "old"));
        history.Add(Text("assistant", "old reply"));
        history.Add(Text("user", "new"));

        Assert.Equal("new", history.Messages[^1].Content[0].Text);
    }

    [Fact]
    public void Trim_NeverLeavesAnOrphanedToolResultAtTheHead()
    {
        // A tool round is user -> assistant(tool_use) -> user(tool_result). Trimming two messages
        // off the front of that leaves a tool_result whose tool_use is gone, and the Messages API
        // rejects the whole request with a 400.
        var history = new ConversationHistory(3);

        history.Add(Text("user", "run the tool"));
        history.Add(ToolUse("tu_1"));
        history.Add(ToolResult("tu_1"));
        history.Add(Text("assistant", "done"));

        Assert.DoesNotContain(
            history.Messages[0].Content,
            block => block.Type == "tool_result");
    }

    [Fact]
    public void Trim_AlwaysStartsOnAUserTurn()
    {
        var history = new ConversationHistory(2);

        history.Add(Text("user", "one"));
        history.Add(ToolUse("tu_1"));
        history.Add(ToolResult("tu_1"));
        history.Add(Text("assistant", "answer"));
        history.Add(Text("user", "two"));

        Assert.NotEmpty(history.Messages);
        Assert.Equal("user", history.Messages[0].Role);
    }

    [Fact]
    public void Trim_KeepsAValidHeadThroughManyToolRounds()
    {
        var history = new ConversationHistory(5);

        history.Add(Text("user", "start"));
        for (int i = 0; i < 20; i++)
        {
            history.Add(ToolUse($"tu_{i}"));
            history.Add(ToolResult($"tu_{i}"));

            Assert.NotEmpty(history.Messages);
            Assert.Equal("user", history.Messages[0].Role);
            Assert.DoesNotContain(history.Messages[0].Content, b => b.Type == "tool_result");
        }
    }

    [Fact]
    public void Trim_ReclaimsTheBacklogOnTheNextOrdinaryUserTurn()
    {
        // Mid tool-loop there is no safe cut point, so the history is allowed over budget. The next
        // plain user message is a legal conversation start, so the backlog gets dropped there.
        var history = new ConversationHistory(4);

        history.Add(Text("user", "start"));
        for (int i = 0; i < 10; i++)
        {
            history.Add(ToolUse($"tu_{i}"));
            history.Add(ToolResult($"tu_{i}"));
        }
        var duringLoop = history.Messages.Count;

        history.Add(Text("assistant", "answer"));
        history.Add(Text("user", "next question"));

        Assert.True(duringLoop > 4, "history is expected to run over budget during a tool loop");
        Assert.True(history.Messages.Count <= 4, $"expected <= 4 after the loop, got {history.Messages.Count}");
        Assert.Equal("user", history.Messages[0].Role);
    }

    [Fact]
    public void Trim_NeverEmptiesANonEmptyHistory()
    {
        // Degenerate input: nothing that qualifies as a conversation start. Keep the messages
        // rather than handing the API an empty array, which it also rejects.
        var history = new ConversationHistory(2);

        history.Add(ToolResult("tu_1"));
        history.Add(ToolResult("tu_2"));

        Assert.NotEmpty(history.Messages);
    }
}

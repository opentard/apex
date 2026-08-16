using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Tard.Agent;
using Tard.Ai;
using Tard.Configuration;
using Tard.Memory;
using Tard.Messaging;
using Tard.Skills;

namespace Tard.Tests.Agent;

/// <summary>
/// The Messages API requires user and assistant turns to alternate. The agent keeps conversation
/// state in memory across messages, so any turn that leaves the history ending on a user message
/// poisons every later message from that sender — the next request opens with two user turns in a
/// row and is rejected, permanently, with no way to recover short of a restart.
/// </summary>
public class TardAgentConversationStateTests
{
    private readonly Mock<IAiClient> _aiClient = new();
    private readonly Mock<IMemoryStore> _memoryStore = new();
    private readonly List<IReadOnlyList<AiMessage>> _sentConversations = new();

    private TardAgent CreateAgent(TardOptions options, params ISkill[] skills)
    {
        _memoryStore.Setup(m => m.ListAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());

        return new TardAgent(
            _aiClient.Object,
            new SkillRegistry(skills),
            _memoryStore.Object,
            Options.Create(options),
            NullLogger<TardAgent>.Instance);
    }

    private void CaptureConversations(Func<int, AiResponse> respond)
    {
        var call = 0;
        _aiClient.Setup(c => c.ChatAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AiMessage>>(),
                It.IsAny<IReadOnlyList<AiTool>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, IReadOnlyList<AiMessage> msgs, IReadOnlyList<AiTool>? _, CancellationToken _) =>
            {
                // Snapshot: the agent mutates the same history instance between calls.
                _sentConversations.Add(msgs.ToList());
                return respond(call++);
            });
    }

    private static AiResponse ToolUse(string id) => new()
    {
        StopReason = "tool_use",
        Content = new[]
        {
            new AiContentBlock
            {
                Type = "tool_use",
                ToolUseId = id,
                ToolName = "echo",
                ToolInput = JsonDocument.Parse("{}").RootElement
            }
        }
    };

    private static AiResponse Text(string text) => new()
    {
        StopReason = "end_turn",
        Content = new[] { new AiContentBlock { Type = "text", Text = text } }
    };

    private static ChatMessage Message(string text, string phone = "+1234567890") => new()
    {
        MessageId = Guid.NewGuid().ToString(),
        FromPhoneNumber = phone,
        SenderName = "Test User",
        MessageType = "text",
        TextBody = text,
        ReceivedAt = DateTimeOffset.UtcNow
    };

    private sealed class EchoSkill : ISkill
    {
        public string Name => "echo";
        public string Description => "echoes";
        public JsonElement ParameterSchema { get; } =
            JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

        public Task<string> ExecuteAsync(JsonElement arguments, SkillContext context, CancellationToken ct = default) =>
            Task.FromResult("""{"ok":true}""");
    }

    [Fact]
    public async Task ProcessMessage_ToolRoundLimit_StillRepliesToTheUser()
    {
        var options = new TardOptions { MaxToolRounds = 3 };
        CaptureConversations(_ => ToolUse("tu"));

        var agent = CreateAgent(options, new EchoSkill());
        var reply = await agent.ProcessMessageAsync(Message("loop forever"));

        Assert.False(string.IsNullOrWhiteSpace(reply));
        Assert.Equal(3, _sentConversations.Count);
    }

    [Fact]
    public async Task ProcessMessage_AfterToolRoundLimit_NextMessageStillAlternatesRoles()
    {
        // Regression: the loop used to bail out leaving the history ending on the tool_result user
        // turn, so the following message produced user -> user and a hard 400 from the API.
        var options = new TardOptions { MaxToolRounds = 2 };
        var exhausted = false;
        CaptureConversations(_ => exhausted ? Text("recovered") : ToolUse("tu"));

        var agent = CreateAgent(options, new EchoSkill());
        await agent.ProcessMessageAsync(Message("burn the rounds"));

        exhausted = true;
        _sentConversations.Clear();
        await agent.ProcessMessageAsync(Message("are you still there?"));

        var conversation = _sentConversations[0];
        Assert.Equal("user", conversation[0].Role);
        for (var i = 1; i < conversation.Count; i++)
        {
            Assert.True(
                conversation[i].Role != conversation[i - 1].Role,
                $"messages {i - 1} and {i} are both '{conversation[i].Role}' — the API rejects that");
        }
    }

    [Fact]
    public async Task ProcessMessage_EveryRequestOpensOnAUserTurn()
    {
        var options = new TardOptions { MaxToolRounds = 4, MaxHistoryPerUser = 4 };
        var round = 0;
        CaptureConversations(_ => round++ % 3 == 2 ? Text("done") : ToolUse($"tu_{round}"));

        var agent = CreateAgent(options, new EchoSkill());
        for (var i = 0; i < 6; i++)
            await agent.ProcessMessageAsync(Message($"message {i}"));

        Assert.NotEmpty(_sentConversations);
        foreach (var conversation in _sentConversations)
        {
            Assert.NotEmpty(conversation);
            Assert.Equal("user", conversation[0].Role);
            Assert.DoesNotContain(conversation[0].Content, b => b.Type == "tool_result");
        }
    }

    [Fact]
    public async Task ProcessMessage_SeparateSendersKeepSeparateHistories()
    {
        CaptureConversations(_ => Text("ok"));
        var agent = CreateAgent(new TardOptions());

        await agent.ProcessMessageAsync(Message("hello from alice", "+1111111111"));
        await agent.ProcessMessageAsync(Message("hello from bob", "+2222222222"));

        // Bob's first request must not carry Alice's turn.
        Assert.Single(_sentConversations[1]);
    }

    [Fact]
    public async Task SeedHistoryIfEmpty_ReplaysAStoredTranscriptIntoTheNextRequest()
    {
        // After a restart the dashboard still shows a full transcript while the agent remembers
        // none of it, so a resumed chat reads as amnesia unless the stored turns are replayed.
        CaptureConversations(_ => Text("ok"));
        var agent = CreateAgent(new TardOptions());

        agent.SeedHistoryIfEmpty("web:abc", new[]
        {
            ("user", "my name is Alice"),
            ("assistant", "Nice to meet you, Alice."),
        });

        await agent.ProcessMessageAsync(Message("what is my name?", "web:abc"));

        var conversation = _sentConversations[0];
        Assert.Equal(3, conversation.Count);
        Assert.Equal("my name is Alice", conversation[0].Content[0].Text);
        Assert.Equal("assistant", conversation[1].Role);
        Assert.Equal("what is my name?", conversation[2].Content[0].Text);
    }

    [Fact]
    public async Task SeedHistoryIfEmpty_DoesNotClobberALiveConversation()
    {
        CaptureConversations(_ => Text("ok"));
        var agent = CreateAgent(new TardOptions());

        await agent.ProcessMessageAsync(Message("first real turn", "web:abc"));

        // A live conversation is authoritative — it holds tool rounds the transcript never records.
        agent.SeedHistoryIfEmpty("web:abc", new[] { ("user", "stale transcript line") });

        _sentConversations.Clear();
        await agent.ProcessMessageAsync(Message("second turn", "web:abc"));

        Assert.DoesNotContain(
            _sentConversations[0],
            m => m.Content.Any(c => c.Text == "stale transcript line"));
    }

    [Fact]
    public async Task SeedHistoryIfEmpty_IgnoresBlankTurns()
    {
        CaptureConversations(_ => Text("ok"));
        var agent = CreateAgent(new TardOptions());

        agent.SeedHistoryIfEmpty("web:abc", new[] { ("user", ""), ("assistant", "   ") });
        await agent.ProcessMessageAsync(Message("hello", "web:abc"));

        Assert.Single(_sentConversations[0]);
    }

    [Fact]
    public async Task ProcessMessage_EvictsColdConversationsPastTheCap()
    {
        var options = new TardOptions { MaxTrackedConversations = 3 };
        CaptureConversations(_ => Text("ok"));
        var agent = CreateAgent(options);

        for (var i = 0; i < 10; i++)
            await agent.ProcessMessageAsync(Message("hi", $"web:chat{i}"));

        // Every sender is new, so an unbounded map would have kept all ten conversations. Prove the
        // oldest were dropped by showing a revisited early sender starts from an empty history.
        _sentConversations.Clear();
        await agent.ProcessMessageAsync(Message("still here?", "web:chat0"));

        Assert.Single(_sentConversations[0]);
    }

    [Fact]
    public async Task ProcessMessage_WebChatsShareOneMemoryScope_ButKeepSeparateHistories()
    {
        // Each dashboard chat is its own sender so histories stay separate, but memories must be
        // shared — otherwise "remember my name" in one chat is invisible in the next.
        CaptureConversations(_ => Text("ok"));
        var agent = CreateAgent(new TardOptions());

        // Registered after CreateAgent, which sets up ListAsync itself.
        var scopes = new List<string>();
        _memoryStore
            .Setup(m => m.ListAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((scope, _) => scopes.Add(scope))
            .ReturnsAsync(new Dictionary<string, string>());

        await agent.ProcessMessageAsync(WebMessage("first", "chatA"));
        await agent.ProcessMessageAsync(WebMessage("second", "chatB"));

        Assert.Equal(new[] { "web", "web" }, scopes);
        // ...and chatB did not inherit chatA's turn.
        Assert.Single(_sentConversations[1]);
    }

    [Fact]
    public async Task ProcessMessage_WhatsAppSenderStillScopesMemoryToItsOwnNumber()
    {
        CaptureConversations(_ => Text("ok"));
        var agent = CreateAgent(new TardOptions());

        var scopes = new List<string>();
        _memoryStore
            .Setup(m => m.ListAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((scope, _) => scopes.Add(scope))
            .ReturnsAsync(new Dictionary<string, string>());

        await agent.ProcessMessageAsync(Message("hi", "+14155550001"));

        Assert.Equal(new[] { "+14155550001" }, scopes);
    }

    private static ChatMessage WebMessage(string text, string chatId) => new()
    {
        MessageId = Guid.NewGuid().ToString(),
        FromPhoneNumber = $"web:{chatId}",
        MemoryScope = "web",
        SenderName = "Web User",
        MessageType = "text",
        TextBody = text,
        ReceivedAt = DateTimeOffset.UtcNow
    };
}

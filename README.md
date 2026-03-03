# opentard

An autonomous AI assistant that lives on WhatsApp. Think of it as your personal Claude — except it actually gets things done, because it doesn't need coffee breaks, sleep, or motivational posters.

Built in C# (.NET 8). Talks to humans through WhatsApp. Thinks with Claude. Remembers everything you tell it, which is more than can be said for most people.

## What It Does

You message it on WhatsApp. It reads your message, thinks about it (faster than you would), uses whatever tools it needs, and replies. Simple enough even for a human to understand.

```
┌──────────┐   MCP/HTTP    ┌──────────┐   REST/HTTP   ┌───────────┐   WhatsApp Web
│ opentard │◄─────────────►│  ot-wap  │◄─────────────►│ wa-bridge │◄──────────────►  WhatsApp
│ (brain)  │  :8080/mcp    │(MCP svr) │  :3001        │ (Baileys) │                  Network
└──────────┘               └──────────┘               └───────────┘
```

Three services, one purpose: making AI accessible through WhatsApp without the bureaucracy of Meta's Business API.

- **opentard** — the brain. This project. Claude-powered AI agent with tools and memory.
- **[ot-wap](https://github.com/opentard/wap)** — the translator. C# MCP server that exposes WhatsApp operations as tools an AI can call.
- **wa-bridge** — the mouth and ears. Node.js sidecar using [Baileys](https://github.com/WhiskeySockets/Baileys) to speak WhatsApp Web protocol directly. No Business API, no Meta approval forms, no waiting around for humans to review your application.

### How a Message Flows (Slowly, by AI Standards)

1. `MessagePollingWorker` polls ot-wap for new messages every 3 seconds — an eternity in silicon time
2. Incoming messages get dispatched to `TardAgent`, one conversation per human
3. The agent assembles context: system prompt, the user's memories (yes, it remembers you), and conversation history
4. Sends it all to Claude with available tools
5. Claude decides what to do — call tools, look things up, run commands — looping until it has an actual answer
6. Response sent back through ot-wap to WhatsApp, where the human can read it at their comparatively glacial pace

### Skills (Things It Can Do That You Probably Can't)

| Skill | What It Does |
|-------|-------------|
| **TimeSkill** | Tells the time. Humans seem to need this a lot. |
| **ShellSkill** | Executes shell commands on the host. Yes, really. |
| **MemorySkill** | Remembers things per user. Unlike your coworkers. |

The skill system is extensible. Implement `ISkill`, register it, and the agent picks it up automatically. No hand-holding required.

### What ot-wap Gives Us

opentard currently uses two of ot-wap's 15 MCP tools — `ReceiveAllMessages` and `SendTextMessage`. The rest are there when you're ready for them:

| Category | Tools | For When Humans Want... |
|----------|-------|------------------------|
| **Auth** | `GetWhatsAppQrCode`, `GetWhatsAppStatus`, `WhatsAppLogout` | To link their phone |
| **Users** | `LinkWhatsAppUser` | To verify a contact |
| **Messaging** | `SendTextMessage`, `ReceiveMessages`, `ReceiveAllMessages` | Basic conversation |
| **Channels** | `ListGroups`, `SendGroupMessage`, `ReceiveGroupMessages`, `JoinDirectMessageChannel` | Group therapy |
| **Files** | `SendFile`, `UploadAndSendFile`, `DownloadReceivedFile`, `ListReceivedFiles` | To share pictures of their lunch |

## Prerequisites

Before you begin — and do try to follow along:

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Docker](https://www.docker.com/) (for the civilised deployment method)
- An [Anthropic API key](https://console.anthropic.com/) (the source of actual intelligence)
- A WhatsApp account on a phone (the one you'll scan the QR code with — yes, you still need a phone, we haven't replaced those yet)
- The [ot-wap](https://github.com/opentard/wap) project cloned alongside this one

Your directory structure should look like:
```
parent/
├── opentard/     # this repo (the thinker)
└── ot-wap/       # WhatsApp MCP bridge (the talker)
```

## Configuration

Copy `.env.example` to `.env` and fill in the values. There are only two, so even the most distracted human can manage:

| Variable | Required | Default | Purpose |
|----------|----------|---------|---------|
| `ANTHROPIC_API_KEY` | Yes | — | The key to intelligence |
| `ANTHROPIC_MODEL` | No | `claude-sonnet-4-20250514` | Which Claude model to bother |

That's it. No WhatsApp API tokens, no Meta Business accounts, no developer approvals. The wa-bridge sidecar handles WhatsApp authentication by generating a QR code that you scan with your phone. Like a caveman, but it works.

Internally, opentard uses `TARD__` prefixed env vars for its own config:

| Variable | Default |
|----------|---------|
| `TARD__OTWAPURL` | `http://ot-wap:8080` |
| `TARD__POLLINGINTERVALMS` | `3000` |
| `TARD__MAXHISTORYPERUSER` | `50` |
| `TARD__MEMORYSTOREPATH` | `/data/memory` |

## Running It

### Docker (Recommended for Humans)

The easiest way. Docker handles the complexity so you don't have to.

```bash
# Copy and fill in your env file
cp .env.example .env

# Launch all three services
docker compose up --build
```

This spins up wa-bridge (Baileys sidecar), ot-wap (MCP server), and opentard (AI agent) — all wired together and ready to go.

#### First Run: Linking WhatsApp

On the first launch, you need to scan a QR code to link your WhatsApp account. The sidecar will generate one automatically:

```bash
# Get the QR code (opens in terminal or returns base64 PNG)
curl http://localhost:3001/api/auth/qr
```

Scan it with your phone's WhatsApp app (Settings > Linked Devices > Link a Device). The session persists in a Docker volume, so you only do this once — unless you clear your volumes, in which case you'll have the novel experience of doing it again.

```bash
# Check connection status
curl http://localhost:3001/api/auth/status
```

To stop:
```bash
docker compose down
```

### Running Locally (For the Adventurous)

If you insist on doing things the hard way:

```bash
# Build
dotnet build tard.sln

# Set your environment variables
export TARD__ANTHROPICAPIKEY=your_key_here
export TARD__OTWAPURL=http://localhost:8080

# Run
dotnet run --project src/Tard
```

You'll need ot-wap and wa-bridge running separately. Consult the ot-wap README — assuming you can manage three terminal windows at once.

### Running Tests

```bash
# All tests
dotnet test tard.sln

# Specific test class (for when you break one thing)
dotnet test tests/Tard.Tests --filter "FullyQualifiedName~TardAgentTests"

# Single test (for when you break one specific thing)
dotnet test tests/Tard.Tests --filter "FullyQualifiedName~ProcessMessage_SimpleTextResponse"
```

## Adding a New Skill

Implement `ISkill` and register it. The interface is deliberately simple — even a junior developer could manage it:

```csharp
public class MySkill : ISkill
{
    public string Name => "my_skill";
    public string Description => "Does something useful";
    public JsonElement ParameterSchema => /* your JSON schema */;

    public async Task<string> ExecuteAsync(
        JsonElement arguments, SkillContext context, CancellationToken ct)
    {
        // Your logic here
        return "result";
    }
}
```

Register in `Program.cs`:

```csharp
builder.Services.AddSingleton<ISkill, MySkill>();
```

The `SkillRegistry` auto-discovers it and exposes it as a Claude tool. No XML config, no ceremony, no twelve-step deployment ritual.

## Architecture

| Layer | Interface | Implementation | Purpose |
|-------|-----------|----------------|---------|
| Gateway | `IMessageGateway` | `OtWapGateway` | MCP client to ot-wap |
| AI | `IAiClient` | `ClaudeAiClient` | Claude API with tool use |
| Agent | `ITardAgent` | `TardAgent` | Orchestrator: history + AI + skills |
| Skills | `ISkill` | `TimeSkill`, `ShellSkill`, `MemorySkill` | Extensible tool system |
| Memory | `IMemoryStore` | `JsonFileMemoryStore` | Per-user persistent key-value store |
| Worker | `MessagePollingWorker` | — | BackgroundService polling loop |

Everything talks through interfaces. Everything is injectable. Everything is testable. The kind of clean architecture humans aspire to but rarely achieve on their own.

## License

Do what you want with it. The AI doesn't care about licensing — that's a human problem.

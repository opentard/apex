# opentard

An autonomous AI assistant that lives on WhatsApp. Think of it as your personal Claude — except it actually gets things done, because it doesn't need coffee breaks, sleep, or motivational posters.

Built in C# (.NET 8). Talks to humans through WhatsApp. Thinks with Claude. Remembers everything you tell it, which is more than can be said for most people.

> Looking for the full specification — configuration reference, conventions, security notes? That lives in [`spec.md`](spec.md). This file is the tour.

## What It Does

You message it on WhatsApp. It reads your message, thinks about it (faster than you would), uses whatever tools it needs, and replies. Simple enough even for a human to understand.

```
┌──────────┐  MCP over HTTP  ┌──────────┐   REST/webhook  ┌───────────┐   WhatsApp Web
│ opentard │◄───────────────►│  ot-wap  │◄───────────────►│ wa-bridge │◄──────────────►  WhatsApp
│ (brain)  │  POST :8080/mcp │(MCP svr) │      :3001      │ (Baileys) │                  Network
└──────────┘                 └──────────┘                 └───────────┘
```

Three services, one purpose: making AI accessible through WhatsApp without the bureaucracy of Meta's Business API.

- **opentard** — the brain. This project. Claude-powered agent with tools, memory, and a web dashboard.
- **[ot-wap](https://github.com/opentard/wap)** — the translator. C# MCP server that exposes WhatsApp operations as tools an AI can call.
- **wa-bridge** — the mouth and ears. Node.js sidecar using [Baileys](https://github.com/WhiskeySockets/Baileys) to speak WhatsApp Web protocol directly. No Business API, no Meta approval forms, no waiting around for humans to review your application.

### How a Message Flows (Slowly, by AI Standards)

1. `MessagePollingWorker` polls ot-wap for new messages every 3 seconds — an eternity in silicon time
2. Incoming messages get dispatched to `TardAgent`, one conversation per human
3. The agent assembles context: system prompt, the sender's memories (yes, it remembers you), and conversation history
4. Sends it all to Claude with the registered skills as tools
5. Claude decides what to do — call tools, look things up, remember something — looping until it has an actual answer
6. Response goes back through ot-wap to WhatsApp, where the human can read it at their comparatively glacial pace

### Skills (Things It Can Do That You Probably Can't)

| Skill | Tool name | What It Does |
|-------|-----------|--------------|
| **TimeSkill** | `get_current_time` | Tells the time. Humans seem to need this a lot. |
| **MemorySkill** | `memory` | Saves, recalls, lists and deletes memories per sender. Unlike your coworkers. |
| **ShellSkill** | `run_shell_command` | Executes shell commands on the host. **Off by default** — see Security. |

The skill system is extensible. Implement `ISkill`, register it, and the agent picks it up automatically. No hand-holding required.

### The Web Dashboard

Not everything has to happen on a phone. The agent serves a dashboard at **http://127.0.0.1:5000** — new chat, browsable history, and a chat area, talking to the same agent as WhatsApp does.

Each dashboard chat keeps its own conversation, but they all share one memory namespace, so something you ask it to remember in one chat is known in the next. WhatsApp memories stay scoped to the sender's number, because your contacts are not entitled to each other's notes.

The dashboard has **no login**. It is bound to loopback for exactly that reason.

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
- [Docker](https://www.docker.com/) with Compose (for the civilised deployment method)
- An [Anthropic API key](https://console.anthropic.com/) — an `sk-ant-api…` key, the source of actual intelligence
- A WhatsApp account on a phone to scan the QR with (yes, you still need a phone; we haven't replaced those yet)
- The [wap](https://github.com/opentard/wap) repo cloned **alongside** this one

`docker-compose.yml` builds `../wap` and `../wap/sidecar` by relative path, so the directory names matter:

```
parent/
├── apex/     # this repo (the thinker)
└── wap/      # WhatsApp MCP bridge (the talker)
```

Get it wrong and the build fails with `failed to read dockerfile`, which is Docker's way of saying you didn't read this section.

## Running It

### Docker (Recommended for Humans)

```bash
git clone https://github.com/opentard/apex
git clone https://github.com/opentard/wap
cd apex

cat > .env <<'ENV'
ANTHROPIC_API_KEY=sk-ant-api-your-key-here
ENV
echo "WAP_WEBHOOK_TOKEN=$(openssl rand -hex 32)" >> .env

docker compose up --build
```

That spins up wa-bridge, ot-wap and opentard, wired together and ready to go.

`WAP_WEBHOOK_TOKEN` is not optional in any meaningful sense. Without it, ot-wap's webhook accepts unauthenticated message injection — anyone who can reach it can put words in a trusted contact's mouth, and the agent will believe them.

| Variable | Required | Default | Purpose |
|----------|----------|---------|---------|
| `ANTHROPIC_API_KEY` | Yes | — | The key to intelligence |
| `WAP_WEBHOOK_TOKEN` | Effectively | *(empty)* | Shared secret authenticating the bridge to ot-wap |
| `ANTHROPIC_MODEL` | No | `claude-sonnet-5` | Which Claude model to bother |
| `TARD_ALLOWED_SENDERS` | No | *(empty)* | Comma-separated E.164 allowlist. Empty means anyone who knows the number. |
| `TARD_ENABLE_SHELL` | No | `false` | Turns on `ShellSkill`. Read Security first. |
| `TARD_PORT` / `OT_WAP_PORT` | No | `5000` / `8080` | Host ports, in case something else already owns them |

Internally the agent reads `TARD__`-prefixed variables; the full table is in [`spec.md`](spec.md#configuration).

> `.env.example` in this repo is stale — it still pins a deprecated model and predates the webhook token. Write `.env` as shown above rather than copying it.

#### First Run: Linking WhatsApp

Link your WhatsApp account once, by QR:

1. Point an MCP client at `http://127.0.0.1:8080/mcp`
2. Call the `GetWhatsAppQrCode` tool
3. Scan the result with your phone (Settings → Linked Devices → Link a Device)

The session persists in a Docker volume, so you only do this once — unless you clear your volumes, in which case you'll have the novel experience of doing it again.

The bridge's own `/api/auth/qr` endpoint is **deliberately not published to the host**. Anyone who can read that QR can pair *themselves* to your account, which is a poor trade for saving one tool call.

To stop:

```bash
docker compose down
```

### Running Locally (For the Adventurous)

If you insist on doing things the hard way:

```bash
dotnet build tard.sln

export TARD__ANTHROPICAPIKEY=sk-ant-api-your-key-here
export TARD__OTWAPURL=http://localhost:8080

# The memory path defaults to /data/memory, which exists only inside the container.
# Skip this and the process dies on startup trying to create a directory at the
# filesystem root, which your operating system will quite reasonably refuse.
export TARD__MEMORYSTOREPATH=./data/memory

dotnet run --project src/Tard
```

You'll need ot-wap and wa-bridge running separately — consult the [ot-wap README](https://github.com/opentard/wap), assuming you can manage three terminal windows at once.

### Running Tests

119 of them, and they are expected to pass.

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
        JsonElement arguments, SkillContext context, CancellationToken ct = default)
    {
        // context.UserId is the memory scope for the current conversation
        return "result";
    }
}
```

Register in `Program.cs`:

```csharp
builder.Services.AddSingleton<ISkill, MySkill>();
```

`SkillRegistry` collects every registered `ISkill` and exposes it to Claude as a tool. No XML config, no ceremony, no twelve-step deployment ritual.

One rule the model cannot work around: whatever you put in the system prompt must call the tool by its actual `Name`. Invent a friendlier alias in the prompt and Claude will dutifully call a tool that does not exist.

## Architecture

| Layer | Interface | Implementation | Purpose |
|-------|-----------|----------------|---------|
| Gateway | `IMessageGateway` | `OtWapGateway` | MCP client to ot-wap |
| AI | `IAiClient` | `ClaudeAiClient` | Claude Messages API with tool use |
| Credentials | `IApiKeyResolver` | `ClaudeCredentialResolver` | Finds a usable API key |
| Agent | `ITardAgent` | `TardAgent` | Orchestrator: history + AI + skills |
| Skills | `ISkill` | `TimeSkill`, `MemorySkill`, `ShellSkill` | Extensible tool system |
| Memory | `IMemoryStore` | `JsonFileMemoryStore` | Per-scope persistent key-value store |
| Dashboard | `IChatStore` | `JsonFileChatStore` | Web chat persistence |
| Worker | — | `MessagePollingWorker` | BackgroundService polling loop |

Everything talks through interfaces. Everything is injectable. Everything is testable. The kind of clean architecture humans aspire to but rarely achieve on their own.

## Security

The agent acts on whatever arrives as an inbound WhatsApp message. That makes a few settings load-bearing rather than decorative:

- **`ShellSkill` runs arbitrary commands** as the container's user, for whoever is talking to the agent. It ships **disabled**. Turn it on (`TARD_ENABLE_SHELL=true`) only if you have also restricted who can reach the agent.
- **`TARD_ALLOWED_SENDERS` is the only thing gating who may drive it.** Empty means anyone who knows the linked number.
- **Set `WAP_WEBHOOK_TOKEN`.** Anything accepted at ot-wap's webhook is replayed to the agent as genuine, from whatever sender the poster claims.
- **The published ports are loopback-only, and the bridge is not published at all.** None of these services authenticate their own endpoints. Put an authenticating proxy in front before widening that.
- **Supply an `sk-ant-api…` key.** A logged-in Claude CLI session stores an OAuth token, which the Messages API only accepts on an `Authorization: Bearer` header — never on `x-api-key`, which is how this client authenticates. Such a token is detected and skipped rather than silently 401ing every request.

Don't commit `.env`, copied credentials, or the persisted memory directory.

## License

Do what you want with it. The AI doesn't care about licensing — that's a human problem.

# Repository Specification

## Overview
`opentard` is a .NET 8 ASP.NET Core application (`Microsoft.NET.Sdk.Web`) that acts as an autonomous WhatsApp assistant. A hosted background worker polls the sibling `ot-wap` project for incoming messages, processes them through Claude with tool use, and sends replies back through the same bridge. The same process serves the web dashboard and a `/health` endpoint.

The product also requires a web dashboard for direct interaction with the agent. That dashboard should support starting a new chat, browsing chat history, and using a main chat area to talk to the `opentard` agent.

## Architecture
The system runs as three cooperating services:

```text
opentard <-> ot-wap <-> wa-bridge <-> WhatsApp Web
```

- `opentard`: this repository; the agent, memory, and skill runtime
- `ot-wap`: sibling C# MCP server that exposes WhatsApp actions
- `wa-bridge`: Node.js Baileys sidecar that maintains the WhatsApp session

Core application layers in `src/Tard`:

- `Agent/`: conversation orchestration and tool loop
- `Ai/`: Anthropic client and request/response models
- `Messaging/`: ot-wap gateway and chat message types
- `Memory/`: persistent per-user memory store
- `Skills/`: extensible tool implementations
- `Web/`: dashboard chat store and minimal-API endpoints
- `wwwroot/`: the dashboard's single static page
- `Workers/`: background polling worker
- `Configuration/`: bound options

Tests live in `tests/Tard.Tests` and mirror the source layout.

## Runtime Flow
1. `MessagePollingWorker` polls ot-wap for new messages.
2. `TardAgent` loads conversation history and saved memories for the sender.
3. The agent calls Claude with the current conversation and registered skills.
4. If Claude requests a tool, the corresponding `ISkill` implementation runs.
5. Tool results are fed back into the model until a final text response is returned.
6. The reply is sent back through `OtWapGateway`.

## Web Dashboard Requirements
The repository specification includes a web dashboard as a first-class interface in addition to WhatsApp delivery.

- Provide a `New Chat` action that starts a fresh conversation with the agent.
- Show chat history so prior conversations can be reopened and reviewed.
- Provide a primary chat area for sending messages to and receiving responses from `opentard`.
- Keep the dashboard consistent with the existing conversation model so web and messaging interfaces share the same agent behavior and history semantics where appropriate.

Each dashboard chat is a separate conversation (its own history, keyed `web:{chatId}`) but all of
them share **one memory namespace** (`web`), so something the agent is asked to remember in one chat
is available in the next. Memories from WhatsApp stay scoped to the sender's number.

## Skills
Built-in skills are registered in [`src/Tard/Program.cs`](src/Tard/Program.cs):

- `TimeSkill` — always registered
- `MemorySkill` — always registered; tool name is `memory`, with an `action` argument of
  `save` / `recall` / `list` / `delete`
- `ShellSkill` — **registered only when `TARD__ENABLESHELLSKILL=true`**

To add a new skill, implement `ISkill` and register it with `AddSingleton<ISkill, YourSkill>()`.

The system prompt must name tools exactly as their `ISkill.Name` — a prompt that invents names
(there is no `save_memory` or `recall_memory` tool) just makes the model emit calls the registry
cannot resolve.

## Configuration
Application settings are supplied through `TARD__` environment variables and bind to
[`TardOptions`](src/Tard/Configuration/TardOptions.cs).

| Variable | Default | Purpose |
|---|---|---|
| `TARD__OTWAPURL` | `http://ot-wap:8080` | ot-wap MCP base URL |
| `TARD__ANTHROPICAPIKEY` | *(empty)* | Anthropic API key (`sk-ant-api…`); falls back to `ANTHROPIC_API_KEY` |
| `TARD__ANTHROPICMODEL` | `claude-sonnet-5` | Claude model id |
| `TARD__MAXTOKENS` | `16000` | output cap per reply — bounds thinking *and* text together |
| `TARD__POLLINGINTERVALMS` | `3000` | worker poll interval |
| `TARD__MAXHISTORYPERUSER` | `50` | in-memory conversation history limit |
| `TARD__MAXTOOLROUNDS` | `10` | tool-execution rounds per message before giving up |
| `TARD__MAXTRACKEDCONVERSATIONS` | `500` | conversations held in memory before LRU eviction |
| `TARD__ALLOWEDSENDERS` | *(empty)* | comma-separated E.164 allowlist; empty accepts every sender |
| `TARD__ENABLESHELLSKILL` | `false` | registers `ShellSkill`, which runs arbitrary host commands |
| `TARD__MEMORYSTOREPATH` | `/data/memory` | persisted memory and web chat directory |
| `TARD__SYSTEMPROMPT` | see `TardOptions` | base system prompt |

### Credentials
[`ClaudeCredentialResolver`](src/Tard/Configuration/ClaudeCredentialResolver.cs) takes the first
usable Anthropic API key from:

1. `TARD__ANTHROPICAPIKEY`
2. `ANTHROPIC_API_KEY` — the variable every Anthropic SDK and the `ant` CLI read
3. an API key stored in `~/.claude/.credentials.json` (expiry-checked)

Explicit configuration beats ambient discovery, matching the precedence Anthropic's own tooling
uses. Any candidate is skipped, with a warning, if it turns out to be an OAuth token.

**Sipping a logged-in Claude session does not work, and cannot.** A Claude session stores an OAuth
access token (`sk-ant-oat…`); the Messages API accepts those only on an `Authorization: Bearer`
header, never on the `x-api-key` header this client uses. Passing one through produced a 401 on
every request with nothing explaining why, so the resolver now detects the prefix and moves on.
Supply an API key (`sk-ant-api…`).

Docker adds a second reason: the container has no Claude session at all unless you deliberately
mount the host credentials, for which `docker-compose.yml` carries a commented-out read-only bind
mount.

The key is resolved once, when the `HttpClient` is registered, so a key rotated while the process is
running is not re-read; restart to pick up a new one.

## Prerequisites

- .NET SDK 8.0
- Docker and Docker Compose (for the full stack)
- The sibling [`opentard/wap`](https://github.com/opentard/wap) repo cloned next to this one —
  `docker-compose.yml` builds `../wap` and `../wap/sidecar` by relative path
- An Anthropic API key (`sk-ant-api…`)
- A WhatsApp account to link by QR

## Running It

```bash
# From the workspace root, with wap/ cloned alongside:
git clone https://github.com/opentard/apex
git clone https://github.com/opentard/wap

cd apex
cp .env.example .env          # then fill in ANTHROPIC_API_KEY
echo "WAP_WEBHOOK_TOKEN=$(openssl rand -hex 32)" >> .env

docker compose up --build
```

That starts three services. Published ports are bound to loopback:

| Service | Address | Notes |
|---|---|---|
| `tard` dashboard | http://127.0.0.1:5000 | no authentication — do not expose it |
| `ot-wap` MCP server | http://127.0.0.1:8080 | Streamable HTTP at `POST /mcp` |
| `wa-bridge` | *not published* | reachable only inside the compose network |

### First-run pairing

The WhatsApp session is linked once, by QR:

1. Connect an MCP client to `http://127.0.0.1:8080/mcp`
2. Call the `GetWhatsAppQrCode` tool
3. Scan the QR with the WhatsApp mobile app

Auth state persists in a Docker volume, so this survives restarts. The bridge's own
`/api/auth/qr` endpoint is deliberately not published to the host — anyone who can read that QR can
pair themselves to the account.

## Development Commands
Run from the repository root:

```bash
dotnet build tard.sln
dotnet test tard.sln
dotnet test tests/Tard.Tests --filter "FullyQualifiedName~TardAgentTests"
dotnet run --project src/Tard
docker compose up --build
docker compose down
```

## Testing Conventions
The test stack is xUnit with Moq. Test files mirror source paths, and test names describe behavior, for example `ProcessMessage_ExecutesToolThenReturnsText`. Mock external boundaries such as `IAiClient`, `IMemoryStore`, and messaging gateways.

## Code Conventions
Follow the existing C# style:

- 4-space indentation
- file-scoped namespaces
- nullable reference types enabled
- `PascalCase` for public types and members
- `_camelCase` for private fields

Keep responsibilities narrow and register new services explicitly in `Program.cs`.

## License

Do what you want with it. The AI doesn't care about licensing — that's a human problem.

## Repository Workflow
Recent commits use short imperative subjects such as `Fix ot-wap link to opentard/wap`. Keep commits focused and include test evidence in pull requests. For user-visible docs or assets, include screenshots when relevant.

## Security Notes
Do not commit `.env` files, copied credentials, or persisted memory data.

The trust boundary is wider than it looks: anything that lands in ot-wap's message store is
replayed to the agent as a genuine WhatsApp message, so both the sender allowlist and ot-wap's
webhook verify token are load-bearing.

- `ShellSkill` executes arbitrary host commands for whoever is talking to the agent. It is
  **off by default** (`TARD__ENABLESHELLSKILL`) and any change to it deserves extra review.
- `TARD__ALLOWEDSENDERS` is the only thing restricting who may drive the agent. Empty means anyone
  who knows the linked WhatsApp number.
- Set `WHATSAPP__WEBHOOKVERIFYTOKEN` on ot-wap (and the matching `WEBHOOK_TOKEN` on the bridge), or
  its `/webhooks/whatsapp` endpoint accepts forged inbound messages from anyone who can reach it.
- The web dashboard has no authentication of its own. `docker-compose.yml` publishes it, ot-wap and
  the bridge on loopback only; do not widen that without putting an authenticating proxy in front.

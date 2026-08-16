using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using Tard.Agent;
using Tard.Ai;
using Tard.Configuration;
using Tard.Memory;
using Tard.Messaging;
using Tard.Skills;
using Tard.Web;
using Tard.Workers;

var builder = WebApplication.CreateBuilder(args);

// Configuration from environment variables (TARD__OTWAPURL, TARD__ANTHROPICAPIKEY, etc.)
builder.Services.Configure<TardOptions>(
    builder.Configuration.GetSection(TardOptions.SectionName));

// API key resolver: TARD__ANTHROPICAPIKEY, then ANTHROPIC_API_KEY, then a key in the Claude session
builder.Services.AddSingleton<IApiKeyResolver, ClaudeCredentialResolver>();

// Claude API HttpClient
builder.Services.AddHttpClient<IAiClient, ClaudeAiClient>((sp, client) =>
{
    var resolver = sp.GetRequiredService<IApiKeyResolver>();
    client.BaseAddress = new Uri("https://api.anthropic.com/");
    client.DefaultRequestHeaders.Add("x-api-key", resolver.Resolve());
    client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    client.DefaultRequestHeaders.Accept.Add(
        new MediaTypeWithQualityHeaderValue("application/json"));
});

// ot-wap gateway (MCP client)
builder.Services.AddSingleton<IMessageGateway, OtWapGateway>();

// Memory store
builder.Services.AddSingleton<IMemoryStore, JsonFileMemoryStore>();

// Skills
builder.Services.AddSingleton<ISkill, TimeSkill>();
builder.Services.AddSingleton<ISkill, MemorySkill>();

// ShellSkill executes arbitrary commands on the host as whatever user the process runs as, on
// behalf of anyone who can message the agent. It stays off unless TARD__ENABLESHELLSKILL=true.
var tardOptions = builder.Configuration
    .GetSection(TardOptions.SectionName).Get<TardOptions>() ?? new TardOptions();
if (tardOptions.EnableShellSkill)
    builder.Services.AddSingleton<ISkill, ShellSkill>();

builder.Services.AddSingleton<SkillRegistry>();

// Agent
builder.Services.AddSingleton<ITardAgent, TardAgent>();

// Web dashboard chat store
builder.Services.AddSingleton<IChatStore, JsonFileChatStore>();

// Polling worker
builder.Services.AddHostedService<MessagePollingWorker>();

var app = builder.Build();

// Serve the dashboard from wwwroot/
app.UseDefaultFiles();
app.UseStaticFiles();

// Chat API
app.MapChatApi();

// Health check (mirrors ot-wap's /health so the container can be probed)
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

var startupLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Tard.Startup");
if (tardOptions.EnableShellSkill)
    startupLog.LogWarning(
        "ShellSkill is ENABLED — every allowed sender can run shell commands on this host.");
if (string.IsNullOrWhiteSpace(tardOptions.AllowedSenders))
    startupLog.LogWarning(
        "TARD__ALLOWEDSENDERS is empty — every WhatsApp sender may talk to this agent. " +
        "Set it to a comma-separated E.164 allowlist to restrict access.");

app.Run();

// Expose for WebApplicationFactory in integration tests
public partial class Program { }

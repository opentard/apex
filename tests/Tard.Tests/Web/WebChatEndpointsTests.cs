using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Tard.Agent;
using Tard.Messaging;
using Tard.Web;

namespace Tard.Tests.Web;

public class WebChatEndpointsTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly Mock<ITardAgent> _mockAgent = new();
    private readonly string _tempDir;

    public WebChatEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"tard-web-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _mockAgent.Setup(a => a.ProcessMessageAsync(It.IsAny<ChatMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Test reply");

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Replace agent with mock
                var agentDesc = services.Single(d => d.ServiceType == typeof(ITardAgent));
                services.Remove(agentDesc);
                services.AddSingleton(_mockAgent.Object);

                // Use temp directory for chat store
                var storeDesc = services.Single(d => d.ServiceType == typeof(IChatStore));
                services.Remove(storeDesc);
                services.AddSingleton<IChatStore>(sp =>
                {
                    var opts = Microsoft.Extensions.Options.Options.Create(
                        new Tard.Configuration.TardOptions { MemoryStorePath = _tempDir });
                    return new JsonFileChatStore(opts);
                });
            });
        });

        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task CreateChat_ReturnsNewSession()
    {
        var response = await _client.PostAsync("/api/chats/", null);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("id", out _));
        Assert.True(body.TryGetProperty("title", out _));
    }

    [Fact]
    public async Task ListChats_ReturnsCreatedSessions()
    {
        await _client.PostAsync("/api/chats/", null);
        await _client.PostAsync("/api/chats/", null);

        var response = await _client.GetAsync("/api/chats");
        response.EnsureSuccessStatusCode();
        var list = await response.Content.ReadFromJsonAsync<JsonElement[]>();

        Assert.NotNull(list);
        Assert.True(list.Length >= 2);
    }

    [Fact]
    public async Task GetChat_ReturnsSessionWithMessages()
    {
        var create = await _client.PostAsync("/api/chats/", null);
        var body = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetString()!;

        var response = await _client.GetAsync($"/api/chats/{id}");
        response.EnsureSuccessStatusCode();
        var chat = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(id, chat.GetProperty("id").GetString());
    }

    [Fact]
    public async Task SendMessage_ReturnsAgentReply()
    {
        var create = await _client.PostAsync("/api/chats/", null);
        var body = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetString()!;

        var response = await _client.PostAsJsonAsync($"/api/chats/{id}/messages", new { text = "hello" });
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Test reply", result.GetProperty("reply").GetString());
    }

    [Fact]
    public async Task SendMessage_UpdatesTitle()
    {
        var create = await _client.PostAsync("/api/chats/", null);
        var body = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetString()!;

        await _client.PostAsJsonAsync($"/api/chats/{id}/messages", new { text = "What is the weather?" });

        var response = await _client.GetAsync($"/api/chats/{id}");
        var chat = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("What is the weather?", chat.GetProperty("title").GetString());
    }

    [Fact]
    public async Task DeleteChat_RemovesSession()
    {
        var create = await _client.PostAsync("/api/chats/", null);
        var body = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetString()!;

        var del = await _client.DeleteAsync($"/api/chats/{id}");
        del.EnsureSuccessStatusCode();

        var get = await _client.GetAsync($"/api/chats/{id}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task SendMessage_ToNonExistentChat_Returns404()
    {
        var response = await _client.PostAsJsonAsync("/api/chats/nonexistent/messages", new { text = "hello" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SendMessage_MissingText_Returns400NotServerError()
    {
        // request.Text bound to null and the auto-title path dereferenced it, so a body with no
        // "text" field crashed the endpoint with a 500 instead of reporting bad input.
        var create = await _client.PostAsync("/api/chats/", null);
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var response = await _client.PostAsJsonAsync($"/api/chats/{id}/messages", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SendMessage_BlankText_Returns400(string text)
    {
        var create = await _client.PostAsync("/api/chats/", null);
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var response = await _client.PostAsJsonAsync($"/api/chats/{id}/messages", new { text });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SendMessage_OverlongText_Returns400()
    {
        var create = await _client.PostAsync("/api/chats/", null);
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var response = await _client.PostAsJsonAsync(
            $"/api/chats/{id}/messages", new { text = new string('x', 40_000) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SendMessage_BlankTextNeverReachesTheAgent()
    {
        var create = await _client.PostAsync("/api/chats/", null);
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        _mockAgent.Invocations.Clear();

        await _client.PostAsJsonAsync($"/api/chats/{id}/messages", new { text = "" });

        _mockAgent.Verify(
            a => a.ProcessMessageAsync(It.IsAny<ChatMessage>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetChat_IdThatSanitizesToNothing_Returns404NotServerError()
    {
        var response = await _client.GetAsync("/api/chats/...");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Health_ReportsHealthy()
    {
        var response = await _client.GetAsync("/health");

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("healthy", body.GetProperty("status").GetString());
    }
}

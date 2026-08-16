using Microsoft.Extensions.Options;
using Tard.Configuration;
using Tard.Web;

namespace Tard.Tests.Web;

public class JsonFileChatStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonFileChatStore _store;

    public JsonFileChatStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"tard-chat-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        var options = Options.Create(new TardOptions { MemoryStorePath = _tempDir });
        _store = new JsonFileChatStore(options);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task CreateAsync_ReturnsSessionWithId()
    {
        var session = await _store.CreateAsync();

        Assert.NotNull(session);
        Assert.False(string.IsNullOrEmpty(session.Id));
        Assert.Equal("New Chat", session.Title);
    }

    [Fact]
    public async Task SaveAndGetAsync_RoundTrips()
    {
        var session = await _store.CreateAsync();
        session.Title = "Test Chat";
        session.Messages.Add(new WebChatMessage { Role = "user", Text = "hello" });
        await _store.SaveAsync(session);

        var loaded = await _store.GetAsync(session.Id);

        Assert.NotNull(loaded);
        Assert.Equal(session.Id, loaded.Id);
        Assert.Equal("Test Chat", loaded.Title);
        Assert.Single(loaded.Messages);
        Assert.Equal("hello", loaded.Messages[0].Text);
    }

    [Fact]
    public async Task ListAsync_ReturnsAllSessions()
    {
        var s1 = await _store.CreateAsync();
        var s2 = await _store.CreateAsync();
        await _store.SaveAsync(s1);
        await _store.SaveAsync(s2);

        var list = await _store.ListAsync();

        Assert.Equal(2, list.Count);
    }

    [Fact]
    public async Task DeleteAsync_RemovesSession()
    {
        var session = await _store.CreateAsync();
        await _store.SaveAsync(session);

        var deleted = await _store.DeleteAsync(session.Id);
        var loaded = await _store.GetAsync(session.Id);

        Assert.True(deleted);
        Assert.Null(loaded);
    }

    [Fact]
    public async Task DeleteAsync_ReturnsFalse_WhenNotFound()
    {
        var result = await _store.DeleteAsync("nonexistent");

        Assert.False(result);
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenNotFound()
    {
        var result = await _store.GetAsync("nonexistent");

        Assert.Null(result);
    }

    [Fact]
    public async Task ListAsync_OrdersByLastMessageDescending()
    {
        var older = await _store.CreateAsync();
        older.LastMessageAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await _store.SaveAsync(older);

        var newer = await _store.CreateAsync();
        newer.LastMessageAt = DateTimeOffset.UtcNow;
        await _store.SaveAsync(newer);

        var list = await _store.ListAsync();

        Assert.Equal(newer.Id, list[0].Id);
        Assert.Equal(older.Id, list[1].Id);
    }

    // --- Listing without materializing history, and safe concurrent updates ---

    [Fact]
    public async Task ListAsync_ReturnsMessageCountsWithoutLoadingMessages()
    {
        var session = await _store.CreateAsync();
        session.Messages.Add(new WebChatMessage { Role = "user", Text = "one" });
        session.Messages.Add(new WebChatMessage { Role = "assistant", Text = "two" });
        await _store.SaveAsync(session);

        var summary = Assert.Single(await _store.ListAsync());

        Assert.Equal(session.Id, summary.Id);
        Assert.Equal(2, summary.MessageCount);
    }

    [Fact]
    public async Task ListAsync_CountsMessagesInFilesWrittenBeforeMessageCountExisted()
    {
        // A file from an older build has no "messageCount" field; the listing must still be right.
        var legacy = """
        {
          "id": "legacychat01",
          "title": "Legacy",
          "createdAt": "2026-01-01T00:00:00+00:00",
          "lastMessageAt": "2026-01-01T00:05:00+00:00",
          "messages": [
            { "role": "user", "text": "a", "timestamp": "2026-01-01T00:01:00+00:00" },
            { "role": "assistant", "text": "b", "timestamp": "2026-01-01T00:02:00+00:00" },
            { "role": "user", "text": "c", "timestamp": "2026-01-01T00:03:00+00:00" }
          ]
        }
        """;
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "chats", "legacychat01.json"), legacy);

        var summary = Assert.Single(await _store.ListAsync());

        Assert.Equal("Legacy", summary.Title);
        Assert.Equal(3, summary.MessageCount);
    }

    [Fact]
    public async Task UpdateAsync_ReturnsNullForUnknownChat()
    {
        Assert.Null(await _store.UpdateAsync("doesnotexist", _ => { }));
    }

    [Fact]
    public async Task UpdateAsync_PersistsTheMutation()
    {
        var session = await _store.CreateAsync();
        await _store.SaveAsync(session);

        await _store.UpdateAsync(session.Id, s => s.Title = "renamed");

        Assert.Equal("renamed", (await _store.GetAsync(session.Id))!.Title);
    }

    [Fact]
    public async Task UpdateAsync_ConcurrentAppendsDoNotLoseMessages()
    {
        // Read-modify-write through Get + Save let two concurrent sends read the same session and
        // clobber each other. Every append must survive.
        var session = await _store.CreateAsync();
        await _store.SaveAsync(session);

        const int writers = 25;
        await Task.WhenAll(Enumerable.Range(0, writers).Select(i =>
            _store.UpdateAsync(session.Id, s => s.Messages.Add(
                new WebChatMessage { Role = "user", Text = $"message {i}" }))));

        var stored = await _store.GetAsync(session.Id);

        Assert.NotNull(stored);
        Assert.Equal(writers, stored.Messages.Count);
        Assert.Equal(writers, stored.MessageCount);
        Assert.Equal(writers, stored.Messages.Select(m => m.Text).Distinct().Count());
    }
}

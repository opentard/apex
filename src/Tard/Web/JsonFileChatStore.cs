using System.Text.Json;
using Microsoft.Extensions.Options;
using Tard.Configuration;

namespace Tard.Web;

public class JsonFileChatStore : IChatStore
{
    private readonly string _directory;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public JsonFileChatStore(IOptions<TardOptions> options)
    {
        _directory = Path.Combine(options.Value.MemoryStorePath, "chats");
        Directory.CreateDirectory(_directory);
    }

    public Task<ChatSession> CreateAsync(CancellationToken ct = default)
    {
        var session = new ChatSession
        {
            Id = Guid.NewGuid().ToString("N")[..12]
        };
        return Task.FromResult(session);
    }

    public async Task<IReadOnlyList<ChatSessionSummary>> ListAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var files = Directory.GetFiles(_directory, "*.json");
            var summaries = new List<ChatSessionSummary>(files.Length);
            foreach (var file in files)
            {
                var json = await File.ReadAllTextAsync(file, ct);
                try
                {
                    // Deserializing into the summary shape lets the serializer skip the message
                    // array outright. The dashboard re-lists after every send, so materializing
                    // every turn of every chat just to render "N msgs" was pure waste.
                    var summary = JsonSerializer.Deserialize<ChatSessionSummary>(json, JsonOpts);
                    if (summary is null)
                        continue;

                    // Files written before messageCount existed need one full read to count.
                    if (summary.MessageCount == 0)
                    {
                        var legacy = JsonSerializer.Deserialize<ChatSession>(json, JsonOpts);
                        if (legacy is not null && legacy.MessageCount is null)
                            summary.MessageCount = legacy.Messages.Count;
                    }

                    summaries.Add(summary);
                }
                catch (JsonException)
                {
                    // One corrupt chat file must not take down the whole sidebar listing.
                }
            }
            return summaries.OrderByDescending(s => s.LastMessageAt).ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ChatSession?> UpdateAsync(
        string chatId,
        Action<ChatSession> mutate,
        CancellationToken ct = default)
    {
        if (!TryGetPath(chatId, out var path))
            return null;

        // Read, mutate and write inside one lock acquisition. Doing this as separate Get/Save calls
        // let two concurrent sends read the same session and clobber each other's message.
        await _lock.WaitAsync(ct);
        try
        {
            if (!File.Exists(path))
                return null;

            ChatSession? session;
            try
            {
                session = JsonSerializer.Deserialize<ChatSession>(
                    await File.ReadAllTextAsync(path, ct), JsonOpts);
            }
            catch (JsonException)
            {
                return null;
            }

            if (session is null)
                return null;

            mutate(session);
            await WriteAsync(session, path, ct);
            return session;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ChatSession?> GetAsync(string chatId, CancellationToken ct = default)
    {
        if (!TryGetPath(chatId, out var path) || !File.Exists(path))
            return null;

        await _lock.WaitAsync(ct);
        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            return JsonSerializer.Deserialize<ChatSession>(json, JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(ChatSession session, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await WriteAsync(session, GetPath(session.Id), ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Writes a session to disk. Caller must hold the lock.</summary>
    private static async Task WriteAsync(ChatSession session, string path, CancellationToken ct)
    {
        session.MessageCount = session.Messages.Count;
        var json = JsonSerializer.Serialize(session, JsonOpts);

        // Write-then-replace: a crash mid-write would otherwise truncate an existing chat.
        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, ct);
        File.Move(tempPath, path, overwrite: true);
    }

    public async Task<bool> DeleteAsync(string chatId, CancellationToken ct = default)
    {
        if (!TryGetPath(chatId, out var path))
            return false;

        await _lock.WaitAsync(ct);
        try
        {
            if (!File.Exists(path))
                return false;

            File.Delete(path);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    private string GetPath(string chatId)
    {
        if (!TryGetPath(chatId, out var path))
            throw new ArgumentException("Chat id contains no usable characters.", nameof(chatId));
        return path;
    }

    /// <summary>
    /// Resolves a chat id to its file. Returns false for ids that sanitize away to nothing, so a
    /// caller can answer 404 instead of surfacing an exception.
    /// </summary>
    private bool TryGetPath(string chatId, out string path)
    {
        var safe = SanitizeId(chatId ?? string.Empty);
        if (safe.Length == 0)
        {
            path = string.Empty;
            return false;
        }

        path = Path.Combine(_directory, $"{safe}.json");
        return true;
    }

    // Keeps ids confined to a single filename: strips separators, dots and traversal sequences.
    private static string SanitizeId(string id) =>
        new(id.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
}

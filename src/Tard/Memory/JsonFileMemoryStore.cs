using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tard.Configuration;

namespace Tard.Memory;

public class JsonFileMemoryStore : IMemoryStore
{
    private readonly string _basePath;
    private readonly ILogger<JsonFileMemoryStore> _logger;
    private static readonly SemaphoreSlim _lock = new(1, 1);

    public JsonFileMemoryStore(IOptions<TardOptions> options, ILogger<JsonFileMemoryStore> logger)
    {
        _basePath = options.Value.MemoryStorePath;
        _logger = logger;
        Directory.CreateDirectory(_basePath);
    }

    // Test-friendly constructor
    public JsonFileMemoryStore(string basePath, ILogger<JsonFileMemoryStore> logger)
    {
        _basePath = basePath;
        _logger = logger;
        Directory.CreateDirectory(_basePath);
    }

    public async Task SaveAsync(string userId, string key, string value, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var data = await LoadUserDataAsync(userId, cancellationToken);
            data[key] = value;
            await SaveUserDataAsync(userId, data, cancellationToken);
            _logger.LogDebug("Saved memory {Key} for user {UserId}", key, userId);
        }
        finally { _lock.Release(); }
    }

    public async Task<string?> RecallAsync(string userId, string key, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var data = await LoadUserDataAsync(userId, cancellationToken);
            return data.TryGetValue(key, out var value) ? value : null;
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyDictionary<string, string>> ListAsync(string userId, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            return await LoadUserDataAsync(userId, cancellationToken);
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteAsync(string userId, string key, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var data = await LoadUserDataAsync(userId, cancellationToken);
            data.Remove(key);
            await SaveUserDataAsync(userId, data, cancellationToken);
        }
        finally { _lock.Release(); }
    }

    private string GetFilePath(string userId)
    {
        // Sanitize userId for filename safety. Stripping separators means two ids that differ only
        // by punctuation share a file, which is acceptable for phone numbers and "web:{chatId}".
        var safe = string.Concat(userId.Where(c => char.IsLetterOrDigit(c) || c == '+' || c == '_'));
        if (safe.Length == 0)
            safe = "unknown";
        return Path.Combine(_basePath, $"{safe}.json");
    }

    private async Task<Dictionary<string, string>> LoadUserDataAsync(string userId, CancellationToken cancellationToken)
    {
        var path = GetFilePath(userId);
        if (!File.Exists(path))
            return new Dictionary<string, string>();

        var json = await File.ReadAllTextAsync(path, cancellationToken);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                ?? new Dictionary<string, string>();
        }
        catch (JsonException ex)
        {
            // A truncated or hand-edited file would otherwise throw on every future memory call,
            // permanently breaking this user's agent. Start fresh instead, keeping the bad file.
            _logger.LogError(ex, "Memory file {Path} is not valid JSON; ignoring it", path);
            return new Dictionary<string, string>();
        }
    }

    private async Task SaveUserDataAsync(string userId, Dictionary<string, string> data, CancellationToken cancellationToken)
    {
        var path = GetFilePath(userId);
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });

        // Write-then-replace so a crash mid-write cannot leave a half-written memory file behind.
        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, cancellationToken);
        File.Move(tempPath, path, overwrite: true);
    }
}

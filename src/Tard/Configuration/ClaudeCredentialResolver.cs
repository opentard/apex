using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Tard.Configuration;

/// <summary>
/// Resolves the Anthropic API key, in order:
/// <list type="number">
///   <item><c>TARD__ANTHROPICAPIKEY</c></item>
///   <item><c>ANTHROPIC_API_KEY</c> — the variable every Anthropic SDK and the <c>ant</c> CLI read</item>
///   <item>an API key sitting in <c>~/.claude/.credentials.json</c></item>
/// </list>
/// <para>
/// A logged-in Claude session stores an OAuth access token rather than an API key. The Messages
/// API only accepts those on <c>Authorization: Bearer</c>, never on the <c>x-api-key</c> header
/// this client uses, so such a token is skipped with a warning instead of being handed back as a
/// credential that is guaranteed to 401.
/// </para>
/// </summary>
public class ClaudeCredentialResolver : IApiKeyResolver
{
    /// <summary>Standard Anthropic environment variable, honoured so this behaves like other tooling.</summary>
    public const string EnvironmentVariableName = "ANTHROPIC_API_KEY";

    private readonly TardOptions _options;
    private readonly ILogger<ClaudeCredentialResolver> _logger;
    private readonly string _credentialsPath;
    private readonly Func<string, string?> _environment;

    public ClaudeCredentialResolver(
        IOptions<TardOptions> options,
        ILogger<ClaudeCredentialResolver> logger)
        : this(options, logger, DefaultCredentialsPath(), Environment.GetEnvironmentVariable)
    {
    }

    /// <summary>Test-friendly constructor that accepts a custom credentials path and environment.</summary>
    internal ClaudeCredentialResolver(
        IOptions<TardOptions> options,
        ILogger<ClaudeCredentialResolver> logger,
        string credentialsPath,
        Func<string, string?>? environment = null)
    {
        _options = options.Value;
        _logger = logger;
        _credentialsPath = credentialsPath;
        _environment = environment ?? (_ => null);
    }

    /// <summary>
    /// Prefix of an OAuth access token. A logged-in Claude session stores one of these, and the
    /// Messages API only accepts it on <c>Authorization: Bearer</c> — never on <c>x-api-key</c>,
    /// where it is a guaranteed 401. Anthropic API keys use the <c>sk-ant-api…</c> prefix instead.
    /// </summary>
    private const string OAuthTokenPrefix = "sk-ant-oat";

    public string Resolve()
    {
        if (TryUse(_options.AnthropicApiKey, "TARD__ANTHROPICAPIKEY", out var configured))
            return configured;

        if (TryUse(_environment(EnvironmentVariableName), EnvironmentVariableName, out var fromEnv))
            return fromEnv;

        if (TryUse(TryReadClaudeToken(), $"the Claude session at {_credentialsPath}", out var fromSession))
            return fromSession;

        _logger.LogWarning(
            "No Anthropic API key found — set TARD__ANTHROPICAPIKEY or {EnvVar} to an sk-ant-api… key",
            EnvironmentVariableName);
        return string.Empty;
    }

    /// <summary>
    /// Accepts a candidate credential unless it is an OAuth token, which cannot authenticate on the
    /// x-api-key header. Returning one anyway produced a 401 on every request with nothing to
    /// explain why, so it is skipped in favour of the next source.
    /// </summary>
    private bool TryUse(string? candidate, string source, out string key)
    {
        key = string.Empty;

        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        if (IsOAuthToken(candidate))
        {
            _logger.LogWarning(
                "Ignoring the credential from {Source}: it is an OAuth session token, which the " +
                "Messages API only accepts on an Authorization: Bearer header — never on " +
                "x-api-key. Supply an Anthropic API key (sk-ant-api…) instead.",
                source);
            return false;
        }

        _logger.LogInformation("Using API key from {Source}", source);
        key = candidate;
        return true;
    }

    internal static bool IsOAuthToken(string? value) =>
        value?.StartsWith(OAuthTokenPrefix, StringComparison.OrdinalIgnoreCase) == true;

    private string? TryReadClaudeToken()
    {
        try
        {
            if (!File.Exists(_credentialsPath))
                return null;

            var json = File.ReadAllText(_credentialsPath);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth))
                return null;

            // Check expiry (milliseconds since epoch)
            if (oauth.TryGetProperty("expiresAt", out var expiresAt))
            {
                var expiryMs = expiresAt.GetInt64();
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (nowMs >= expiryMs)
                {
                    _logger.LogWarning("Claude session token expired, falling back to config key");
                    return null;
                }
            }

            if (oauth.TryGetProperty("accessToken", out var token))
            {
                var value = token.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read Claude credentials from {Path}", _credentialsPath);
            return null;
        }
    }

    private static string DefaultCredentialsPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".claude", ".credentials.json");
    }
}

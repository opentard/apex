using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tard.Configuration;

namespace Tard.Tests.Configuration;

public class ClaudeCredentialResolverTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ILogger<ClaudeCredentialResolver> _logger = NullLogger<ClaudeCredentialResolver>.Instance;

    public ClaudeCredentialResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"tard-cred-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteCreds(string json)
    {
        var path = Path.Combine(_tempDir, ".credentials.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static IOptions<TardOptions> Opts(string key = "") =>
        Options.Create(new TardOptions { AnthropicApiKey = key });

    [Fact]
    public void Resolve_PrefersExplicitConfigKey_OverClaudeSession()
    {
        // Explicit configuration beats ambient discovery — the same precedence the Anthropic SDKs
        // and the `ant` CLI use, where a set API key shadows a logged-in profile.
        var futureMs = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds();
        var path = WriteCreds($$"""
        {
            "claudeAiOauth": {
                "accessToken": "sk-ant-session-token",
                "expiresAt": {{futureMs}}
            }
        }
        """);

        var resolver = new ClaudeCredentialResolver(Opts("sk-ant-api03-config"), _logger, path);

        Assert.Equal("sk-ant-api03-config", resolver.Resolve());
    }

    [Fact]
    public void Resolve_FallsBackToClaudeSession_WhenNothingElseIsConfigured()
    {
        var path = WriteCreds("""
        {
            "claudeAiOauth": { "accessToken": "sk-ant-api03-from-session" }
        }
        """);

        var resolver = new ClaudeCredentialResolver(Opts(""), _logger, path);

        Assert.Equal("sk-ant-api03-from-session", resolver.Resolve());
    }

    [Fact]
    public void Resolve_UsesAnthropicApiKeyEnvVar_WhenConfigIsEmpty()
    {
        var path = Path.Combine(_tempDir, "nonexistent.json");
        var env = (string name) =>
            name == ClaudeCredentialResolver.EnvironmentVariableName ? "sk-ant-api03-from-env" : null;

        var resolver = new ClaudeCredentialResolver(Opts(""), _logger, path, env);

        Assert.Equal("sk-ant-api03-from-env", resolver.Resolve());
    }

    [Fact]
    public void Resolve_ConfigKeyBeatsEnvironmentVariable()
    {
        var path = Path.Combine(_tempDir, "nonexistent.json");
        var env = (string _) => "sk-ant-api03-from-env";

        var resolver = new ClaudeCredentialResolver(Opts("sk-ant-api03-config"), _logger, path, env);

        Assert.Equal("sk-ant-api03-config", resolver.Resolve());
    }

    [Fact]
    public void Resolve_SkipsOAuthTokenInConfig_AndFallsThroughToEnvironment()
    {
        var path = Path.Combine(_tempDir, "nonexistent.json");
        var env = (string _) => "sk-ant-api03-from-env";

        var resolver = new ClaudeCredentialResolver(Opts("sk-ant-oat01-wrong"), _logger, path, env);

        Assert.Equal("sk-ant-api03-from-env", resolver.Resolve());
    }

    [Fact]
    public void Resolve_FallsBackToConfigKey_WhenNoCredentialsFile()
    {
        var path = Path.Combine(_tempDir, "nonexistent.json");

        var resolver = new ClaudeCredentialResolver(Opts("sk-ant-config-key"), _logger, path);

        Assert.Equal("sk-ant-config-key", resolver.Resolve());
    }

    [Fact]
    public void Resolve_FallsBackToConfigKey_WhenTokenExpired()
    {
        var pastMs = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds();
        var path = WriteCreds($$"""
        {
            "claudeAiOauth": {
                "accessToken": "sk-ant-expired",
                "expiresAt": {{pastMs}}
            }
        }
        """);

        var resolver = new ClaudeCredentialResolver(Opts("sk-ant-config-key"), _logger, path);

        Assert.Equal("sk-ant-config-key", resolver.Resolve());
    }

    [Fact]
    public void Resolve_FallsBackToConfigKey_WhenJsonMalformed()
    {
        var path = WriteCreds("not valid json {{{");

        var resolver = new ClaudeCredentialResolver(Opts("sk-ant-config-key"), _logger, path);

        Assert.Equal("sk-ant-config-key", resolver.Resolve());
    }

    [Fact]
    public void Resolve_FallsBackToConfigKey_WhenOauthSectionMissing()
    {
        var path = WriteCreds("""{ "otherSection": {} }""");

        var resolver = new ClaudeCredentialResolver(Opts("sk-ant-config-key"), _logger, path);

        Assert.Equal("sk-ant-config-key", resolver.Resolve());
    }

    [Fact]
    public void Resolve_ReturnsEmpty_WhenNoKeyAvailable()
    {
        var path = Path.Combine(_tempDir, "nonexistent.json");

        var resolver = new ClaudeCredentialResolver(Opts(""), _logger, path);

        Assert.Equal(string.Empty, resolver.Resolve());
    }

    [Fact]
    public void Resolve_UsesSessionToken_WhenNoExpiryField()
    {
        var path = WriteCreds("""
        {
            "claudeAiOauth": {
                "accessToken": "sk-ant-api03-no-expiry"
            }
        }
        """);

        var resolver = new ClaudeCredentialResolver(Opts(""), _logger, path);

        Assert.Equal("sk-ant-api03-no-expiry", resolver.Resolve());
    }

    // A logged-in Claude session stores an OAuth access token (sk-ant-oat…). The Messages API only
    // accepts that on Authorization: Bearer — presented as x-api-key it is a guaranteed 401, so the
    // resolver must skip it rather than hand back a credential that cannot authenticate.

    [Fact]
    public void Resolve_SkipsOAuthSessionToken_AndFallsBackToConfiguredApiKey()
    {
        var futureMs = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds();
        var path = WriteCreds($$"""
        {
            "claudeAiOauth": {
                "accessToken": "sk-ant-oat01-abc123",
                "expiresAt": {{futureMs}}
            }
        }
        """);

        var resolver = new ClaudeCredentialResolver(Opts("sk-ant-api03-real-key"), _logger, path);

        Assert.Equal("sk-ant-api03-real-key", resolver.Resolve());
    }

    [Fact]
    public void Resolve_OAuthSessionTokenWithNoFallback_ReturnsEmpty()
    {
        var path = WriteCreds("""
        {
            "claudeAiOauth": { "accessToken": "sk-ant-oat01-abc123" }
        }
        """);

        var resolver = new ClaudeCredentialResolver(Opts(""), _logger, path);

        Assert.Equal(string.Empty, resolver.Resolve());
    }

    [Theory]
    [InlineData("sk-ant-oat01-abc", true)]
    [InlineData("SK-ANT-OAT01-ABC", true)]
    [InlineData("sk-ant-api03-abc", false)]
    [InlineData("sk-ant-something", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsOAuthToken_RecognisesOnlyTheOAuthPrefix(string? value, bool expected)
    {
        Assert.Equal(expected, ClaudeCredentialResolver.IsOAuthToken(value));
    }
}

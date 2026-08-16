namespace Tard.Configuration;

public interface IApiKeyResolver
{
    /// <summary>
    /// Resolves the Anthropic API key, taking the first usable value from
    /// <c>TARD__ANTHROPICAPIKEY</c>, then the <c>ANTHROPIC_API_KEY</c> environment variable, then
    /// an API key found in the logged-in Claude session file. Explicit configuration wins over
    /// ambient discovery.
    /// <para>
    /// OAuth session tokens are skipped rather than returned: the Messages API accepts those only
    /// on an <c>Authorization: Bearer</c> header, never on the <c>x-api-key</c> header this client
    /// uses, so returning one would 401 every request. Returns an empty string when nothing usable
    /// is configured.
    /// </para>
    /// </summary>
    string Resolve();
}

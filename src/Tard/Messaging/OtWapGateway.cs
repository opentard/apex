using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Tard.Configuration;

namespace Tard.Messaging;

public class OtWapGateway : IMessageGateway, IAsyncDisposable
{
    /// <summary>
    /// ot-wap serializes its tool payloads with an anonymous wrapper (camelCase "count"/"messages")
    /// around PascalCase StoredMessage bodies. System.Text.Json is case-sensitive by default, so a
    /// strict reader silently binds Messages to null and the agent sees no traffic at all.
    /// Parse case-insensitively so the gateway tolerates either casing on either level.
    /// </summary>
    private static readonly JsonSerializerOptions McpJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly TardOptions _options;
    private readonly ILogger<OtWapGateway> _logger;
    private IMcpClient? _client;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public OtWapGateway(IOptions<TardOptions> options, ILogger<OtWapGateway> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    private async Task<IMcpClient> GetClientAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
            return _client;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_client is not null)
                return _client;

            _logger.LogInformation("Connecting MCP client to {Url}", _options.OtWapUrl);

            // ot-wap's MapMcp("/mcp") serves Streamable HTTP on POST /mcp and only exposes the
            // legacy SSE stream at /mcp/sse. Left in legacy mode the client issues GET /mcp, gets
            // a 404, and every poll fails — so ask for Streamable HTTP explicitly.
            _client = await McpClientFactory.CreateAsync(
                new SseClientTransport(new SseClientTransportOptions
                {
                    Endpoint = new Uri($"{_options.OtWapUrl.TrimEnd('/')}/mcp"),
                    UseStreamableHttp = true,
                    Name = "tard-agent"
                }),
                cancellationToken: cancellationToken);

            return _client;
        }
        finally { _initLock.Release(); }
    }

    public async Task<IReadOnlyList<ChatMessage>> GetNewMessagesAsync(
        DateTimeOffset? since = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = await GetClientAsync(cancellationToken);

            var args = new Dictionary<string, object?>();
            if (since.HasValue)
                args["since"] = since.Value.ToString("o");

            var result = await client.CallToolAsync("ReceiveAllMessages", args, cancellationToken: cancellationToken);

            var text = result.Content.FirstOrDefault(c => c.Type == "text")?.Text;
            return ParseMessagesPayload(text);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get messages from ot-wap");
            // Reset client on failure so it reconnects next time
            await DisposeClientAsync();
            return Array.Empty<ChatMessage>();
        }
    }

    public async Task SendMessageAsync(
        string recipientPhoneNumber,
        string text,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = await GetClientAsync(cancellationToken);
            await client.CallToolAsync("SendTextMessage",
                new Dictionary<string, object?>
                {
                    ["recipientPhoneNumber"] = recipientPhoneNumber,
                    ["message"] = text
                },
                cancellationToken: cancellationToken);

            _logger.LogInformation("Sent message to {Phone}", recipientPhoneNumber);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send message to {Phone}", recipientPhoneNumber);
            await DisposeClientAsync();
            throw;
        }
    }

    private async Task DisposeClientAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeClientAsync();
        _initLock.Dispose();
    }

    /// <summary>
    /// Turns the JSON body of ot-wap's ReceiveAllMessages tool into chat messages.
    /// Split out from the transport so the wire contract between the two services is unit-testable
    /// — this is exactly where a silent casing mismatch stopped every inbound message.
    /// </summary>
    internal static IReadOnlyList<ChatMessage> ParseMessagesPayload(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<ChatMessage>();

        var parsed = JsonSerializer.Deserialize<McpMessagesResponse>(text, McpJsonOptions);
        if (parsed?.Messages is null)
            return Array.Empty<ChatMessage>();

        return parsed.Messages
            .Where(m => !string.IsNullOrEmpty(m.MessageId))
            .Select(m => new ChatMessage
            {
                MessageId = m.MessageId,
                FromPhoneNumber = m.FromPhoneNumber,
                SenderName = m.SenderName ?? "Unknown",
                MessageType = m.MessageType,
                TextBody = m.TextBody,
                MediaId = m.MediaId,
                GroupId = m.GroupId,
                ReceivedAt = m.ReceivedAt
            })
            .ToList();
    }

    // DTOs for parsing MCP tool responses
    private class McpMessagesResponse
    {
        public int Count { get; set; }
        public List<McpStoredMessage>? Messages { get; set; }
    }

    private class McpStoredMessage
    {
        public string MessageId { get; set; } = "";
        public string FromPhoneNumber { get; set; } = "";
        public string? SenderName { get; set; }
        public string MessageType { get; set; } = "";
        public string? TextBody { get; set; }
        public string? MediaId { get; set; }
        public string? GroupId { get; set; }
        public DateTimeOffset ReceivedAt { get; set; }
    }
}

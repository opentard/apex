using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tard.Ai.Models;
using Tard.Configuration;

namespace Tard.Ai;

public class ClaudeAiClient : IAiClient
{
    private readonly HttpClient _httpClient;
    private readonly TardOptions _options;
    private readonly ILogger<ClaudeAiClient> _logger;

    public ClaudeAiClient(HttpClient httpClient, IOptions<TardOptions> options, ILogger<ClaudeAiClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AiResponse> ChatAsync(
        string systemPrompt,
        IReadOnlyList<AiMessage> messages,
        IReadOnlyList<AiTool>? tools = null,
        CancellationToken cancellationToken = default)
    {
        var claudeMessages = messages.Select(ToClaudeMessage).ToList();

        var request = new ClaudeRequest
        {
            Model = _options.AnthropicModel,
            MaxTokens = _options.MaxTokens,
            System = systemPrompt,
            Messages = claudeMessages,
            Tools = tools?.Select(t => new ClaudeTool
            {
                Name = t.Name,
                Description = t.Description,
                InputSchema = t.InputSchema
            }).ToList()
        };

        var response = await _httpClient.PostAsJsonAsync("v1/messages", request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // EnsureSuccessStatusCode() drops the body, which is where the Anthropic API explains
            // what it actually rejected (bad model name, malformed tool_result, expired key...).
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Claude API returned {Status}: {Body}", (int)response.StatusCode, body);
            throw new HttpRequestException(
                $"Claude API returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}",
                null, response.StatusCode);
        }

        var claudeResponse = await response.Content.ReadFromJsonAsync<ClaudeResponse>(cancellationToken: cancellationToken);
        if (claudeResponse is null)
            throw new InvalidOperationException("Empty response from Claude API");

        _logger.LogDebug("Claude API: {InputTokens} in, {OutputTokens} out, stop={StopReason}",
            claudeResponse.Usage?.InputTokens, claudeResponse.Usage?.OutputTokens, claudeResponse.StopReason);

        return new AiResponse
        {
            StopReason = claudeResponse.StopReason ?? "end_turn",
            Content = claudeResponse.Content.Select(ToAiContentBlock).ToList()
        };
    }

    private static ClaudeMessage ToClaudeMessage(AiMessage msg)
    {
        var blocks = msg.Content.Select(b =>
        {
            if (b.Type == "tool_use")
                return new ClaudeContentBlock
                {
                    Type = "tool_use",
                    Id = b.ToolUseId,
                    Name = b.ToolName,
                    Input = b.ToolInput
                };
            if (b.Type == "tool_result")
                return new ClaudeContentBlock
                {
                    Type = "tool_result",
                    ToolUseId = b.ToolUseId,
                    ToolResultContent = b.ToolResultContent
                };
            return new ClaudeContentBlock
            {
                Type = "text",
                Text = b.Text
            };
        }).ToList();

        return new ClaudeMessage
        {
            Role = msg.Role,
            Content = blocks
        };
    }

    private static AiContentBlock ToAiContentBlock(ClaudeResponseContent c)
    {
        if (c.Type == "tool_use")
            return new AiContentBlock
            {
                Type = "tool_use",
                ToolUseId = c.Id,
                ToolName = c.Name,
                ToolInput = c.Input
            };

        return new AiContentBlock
        {
            Type = "text",
            Text = c.Text
        };
    }
}

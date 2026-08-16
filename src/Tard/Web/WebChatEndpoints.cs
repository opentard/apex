using Microsoft.AspNetCore.Mvc;
using Tard.Agent;
using Tard.Messaging;

namespace Tard.Web;

public static class WebChatEndpoints
{
    public static void MapChatApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/chats");

        api.MapPost("/", CreateChat);
        api.MapGet("/", ListChats);
        api.MapGet("/{chatId}", GetChat);
        api.MapPost("/{chatId}/messages", SendMessage);
        api.MapDelete("/{chatId}", DeleteChat);
    }

    private static async Task<IResult> CreateChat(IChatStore store, CancellationToken ct)
    {
        var session = await store.CreateAsync(ct);
        await store.SaveAsync(session, ct);
        return Results.Ok(new { session.Id, session.Title, session.CreatedAt });
    }

    private static async Task<IResult> ListChats(IChatStore store, CancellationToken ct)
    {
        var summaries = await store.ListAsync(ct);
        return Results.Ok(summaries.Select(s => new
        {
            s.Id,
            s.Title,
            s.CreatedAt,
            s.LastMessageAt,
            s.MessageCount
        }));
    }

    private static async Task<IResult> GetChat(string chatId, IChatStore store, CancellationToken ct)
    {
        var session = await store.GetAsync(chatId, ct);
        return session is null ? Results.NotFound() : Results.Ok(session);
    }

    /// <summary>Upper bound on a single dashboard message, to keep one request from blowing the context window.</summary>
    private const int MaxMessageLength = 32_000;

    /// <summary>Memory namespace shared by every dashboard chat.</summary>
    private const string WebMemoryScope = "web";

    private static async Task<IResult> SendMessage(
        string chatId,
        [FromBody] SendMessageRequest? request,
        IChatStore store,
        ITardAgent agent,
        CancellationToken ct)
    {
        // A missing body or a null/blank text binds to null and would otherwise NRE into a 500.
        if (request is null || string.IsNullOrWhiteSpace(request.Text))
            return Results.BadRequest(new { error = "text is required." });

        if (request.Text.Length > MaxMessageLength)
            return Results.BadRequest(new { error = $"text exceeds the {MaxMessageLength} character limit." });

        // Record the user's turn under the store lock, so two concurrent sends cannot read the same
        // session and overwrite one another. This also persists the message *before* the agent runs
        // — previously a failed or slow agent call lost the user's message entirely.
        var session = await store.UpdateAsync(chatId, s =>
        {
            s.Messages.Add(new WebChatMessage
            {
                Role = "user",
                Text = request.Text,
                Timestamp = DateTimeOffset.UtcNow
            });

            // Auto-title from first message
            if (s.Messages.Count == 1)
            {
                s.Title = request.Text.Length > 40
                    ? request.Text[..40] + "..."
                    : request.Text;
            }

            s.LastMessageAt = DateTimeOffset.UtcNow;
        }, ct);

        if (session is null)
            return Results.NotFound();

        // Replay the stored transcript into the agent. Without it, a chat resumed after a restart
        // shows the user a full history the agent has no memory of. Exclude the turn just added —
        // the agent appends that itself.
        agent.SeedHistoryIfEmpty(
            $"web:{chatId}",
            session.Messages.Take(session.Messages.Count - 1).Select(m => (m.Role, m.Text)));

        // Process through the agent using a synthetic ChatMessage. The sender is per-chat so each
        // chat keeps its own history, but the memory scope is shared across the dashboard —
        // otherwise "remember this" in one chat would be invisible in the next.
        var chatMessage = new ChatMessage
        {
            MessageId = $"web-{chatId}-{session.Messages.Count}",
            FromPhoneNumber = $"web:{chatId}",
            MemoryScope = WebMemoryScope,
            SenderName = "Web User",
            MessageType = "text",
            TextBody = request.Text,
            ReceivedAt = DateTimeOffset.UtcNow
        };

        var reply = await agent.ProcessMessageAsync(chatMessage, ct);

        await store.UpdateAsync(chatId, s =>
        {
            s.Messages.Add(new WebChatMessage
            {
                Role = "assistant",
                Text = reply,
                Timestamp = DateTimeOffset.UtcNow
            });
            s.LastMessageAt = DateTimeOffset.UtcNow;
        }, ct);

        return Results.Ok(new { reply });
    }

    private static async Task<IResult> DeleteChat(string chatId, IChatStore store, CancellationToken ct)
    {
        var deleted = await store.DeleteAsync(chatId, ct);
        return deleted ? Results.Ok() : Results.NotFound();
    }
}

public record SendMessageRequest(string Text);

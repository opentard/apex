using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tard.Agent;
using Tard.Configuration;
using Tard.Messaging;

namespace Tard.Workers;

public class MessagePollingWorker : BackgroundService
{
    private const int MaxProcessedIdsTracked = 10_000;

    private readonly IMessageGateway _gateway;
    private readonly ITardAgent _agent;
    private readonly TardOptions _options;
    private readonly ILogger<MessagePollingWorker> _logger;

    // Insertion-ordered so the oldest ids can be evicted individually instead of clearing the
    // whole set — a blanket Clear() lets messages still inside the `since` window be answered twice.
    private readonly HashSet<string> _processedMessageIds = new();
    private readonly Queue<string> _processedMessageOrder = new();

    private readonly HashSet<string> _allowedSenders;
    private DateTimeOffset _lastPollTime = DateTimeOffset.UtcNow;

    public MessagePollingWorker(
        IMessageGateway gateway,
        ITardAgent agent,
        IOptions<TardOptions> options,
        ILogger<MessagePollingWorker> logger)
    {
        _gateway = gateway;
        _agent = agent;
        _options = options.Value;
        _logger = logger;
        _allowedSenders = ParseAllowedSenders(_options.AllowedSenders);
    }

    internal static HashSet<string> ParseAllowedSenders(string? raw) =>
        (raw ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Empty allowlist means "no restriction configured", so every sender is accepted.</summary>
    internal bool IsSenderAllowed(string phoneNumber) =>
        _allowedSenders.Count == 0 || _allowedSenders.Contains(phoneNumber);

    private void MarkProcessed(string messageId)
    {
        _processedMessageOrder.Enqueue(messageId);
        while (_processedMessageOrder.Count > MaxProcessedIdsTracked)
            _processedMessageIds.Remove(_processedMessageOrder.Dequeue());
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("tard agent starting — polling ot-wap every {Interval}ms", _options.PollingIntervalMs);

        // Small startup delay to let ot-wap come up
        await Task.Delay(2000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var messages = await _gateway.GetNewMessagesAsync(_lastPollTime, stoppingToken);

                foreach (var msg in messages)
                {
                    if (!_processedMessageIds.Add(msg.MessageId))
                        continue; // Already processed

                    MarkProcessed(msg.MessageId);

                    _logger.LogInformation("New message from {Sender} ({Phone}): {Type}",
                        msg.SenderName, msg.FromPhoneNumber, msg.MessageType);

                    // Group messages are addressed to the whole group, not to the agent.
                    if (msg.GroupId is not null)
                    {
                        _logger.LogDebug("Skipping group message {Id}", msg.MessageId);
                        continue;
                    }

                    if (!IsSenderAllowed(msg.FromPhoneNumber))
                    {
                        _logger.LogWarning("Ignoring message {Id} from non-allowlisted sender {Phone}",
                            msg.MessageId, msg.FromPhoneNumber);
                        continue;
                    }

                    try
                    {
                        var response = await _agent.ProcessMessageAsync(msg, stoppingToken);
                        await _gateway.SendMessageAsync(msg.FromPhoneNumber, response, stoppingToken);
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        _logger.LogError(ex, "Failed to process message {Id} from {Phone}",
                            msg.MessageId, msg.FromPhoneNumber);

                        // The message is already marked processed, so staying silent leaves the
                        // sender waiting forever on a reply that will never be retried.
                        try
                        {
                            await _gateway.SendMessageAsync(
                                msg.FromPhoneNumber,
                                "Sorry — something went wrong handling that message. Please try again.",
                                stoppingToken);
                        }
                        catch (Exception notifyEx)
                        {
                            _logger.LogError(notifyEx, "Could not deliver the failure notice to {Phone}",
                                msg.FromPhoneNumber);
                        }
                    }
                }

                if (messages.Count > 0)
                {
                    // ot-wap filters with `since >=`, so advancing by a tick stops the newest
                    // message from being refetched on every single poll.
                    _lastPollTime = messages.Max(m => m.ReceivedAt).AddTicks(1);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Polling loop error");
            }

            await Task.Delay(_options.PollingIntervalMs, stoppingToken);
        }
    }
}

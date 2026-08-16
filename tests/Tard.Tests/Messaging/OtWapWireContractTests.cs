using System.Text.Json;
using Tard.Messaging;

namespace Tard.Tests.Messaging;

/// <summary>
/// Guards the JSON contract between ot-wap's MCP tools and this agent.
/// <para>
/// ot-wap serializes with <c>JsonSerializer.Serialize(new { count = messages.Count, messages })</c>,
/// which emits a camelCase wrapper around PascalCase message bodies. The gateway used to parse it
/// with case-sensitive defaults, so <c>Messages</c> silently bound to null and the agent received
/// nothing at all — no exception, no log, just permanent silence. These tests pin the real wire
/// format so that regression cannot come back unnoticed.
/// </para>
/// </summary>
public class OtWapWireContractTests
{
    /// <summary>Byte-for-byte what ot-wap's MessagingTools.ReceiveAllMessages returns.</summary>
    private static string OtWapWireFormat(params object[] messages) =>
        JsonSerializer.Serialize(new { count = messages.Length, messages });

    private static object StoredMessage(
        string messageId = "wamid.ABC",
        string from = "+14155550001",
        string? text = "hello tard",
        string? groupId = null) => new
        {
            MessageId = messageId,
            FromPhoneNumber = from,
            SenderName = "Alice",
            MessageType = "text",
            TextBody = text,
            MediaId = (string?)null,
            MediaMimeType = (string?)null,
            MediaCaption = (string?)null,
            GroupId = groupId,
            ReceivedAt = DateTimeOffset.Parse("2026-01-01T12:00:00+00:00")
        };

    [Fact]
    public void ParseMessagesPayload_ParsesRealOtWapPayload()
    {
        var payload = OtWapWireFormat(StoredMessage());

        var messages = OtWapGateway.ParseMessagesPayload(payload);

        var message = Assert.Single(messages);
        Assert.Equal("wamid.ABC", message.MessageId);
        Assert.Equal("+14155550001", message.FromPhoneNumber);
        Assert.Equal("Alice", message.SenderName);
        Assert.Equal("text", message.MessageType);
        Assert.Equal("hello tard", message.TextBody);
        Assert.Null(message.GroupId);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T12:00:00+00:00"), message.ReceivedAt);
    }

    [Fact]
    public void ParseMessagesPayload_ParsesAllCamelCasePayload()
    {
        // Tolerate a future ot-wap that serializes everything camelCase.
        var payload = """
            {"count":1,"messages":[{"messageId":"wamid.XYZ","fromPhoneNumber":"+14155550002",
            "senderName":"Bob","messageType":"image","textBody":null,"mediaId":"m1",
            "groupId":null,"receivedAt":"2026-01-01T12:00:00+00:00"}]}
            """;

        var message = Assert.Single(OtWapGateway.ParseMessagesPayload(payload));

        Assert.Equal("wamid.XYZ", message.MessageId);
        Assert.Equal("Bob", message.SenderName);
        Assert.Equal("image", message.MessageType);
        Assert.Equal("m1", message.MediaId);
    }

    [Fact]
    public void ParseMessagesPayload_ReadsMultipleMessagesInOrder()
    {
        var payload = OtWapWireFormat(
            StoredMessage("wamid.1", text: "first"),
            StoredMessage("wamid.2", text: "second"));

        var messages = OtWapGateway.ParseMessagesPayload(payload);

        Assert.Equal(2, messages.Count);
        Assert.Equal("first", messages[0].TextBody);
        Assert.Equal("second", messages[1].TextBody);
    }

    [Fact]
    public void ParseMessagesPayload_KeepsGroupIdSoTheWorkerCanSkipGroupChats()
    {
        var payload = OtWapWireFormat(StoredMessage(groupId: "1203630"));

        Assert.Equal("1203630", Assert.Single(OtWapGateway.ParseMessagesPayload(payload)).GroupId);
    }

    [Fact]
    public void ParseMessagesPayload_DefaultsMissingSenderName()
    {
        var payload = """{"count":1,"messages":[{"MessageId":"m","FromPhoneNumber":"+1","MessageType":"text"}]}""";

        Assert.Equal("Unknown", Assert.Single(OtWapGateway.ParseMessagesPayload(payload)).SenderName);
    }

    [Fact]
    public void ParseMessagesPayload_DropsEntriesWithNoMessageId()
    {
        // Without an id the polling worker's dedupe key is empty and every poll re-answers it.
        var payload = """{"count":1,"messages":[{"FromPhoneNumber":"+1","MessageType":"text"}]}""";

        Assert.Empty(OtWapGateway.ParseMessagesPayload(payload));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("""{"count":0,"messages":[]}""")]
    [InlineData("""{"count":0}""")]
    public void ParseMessagesPayload_EmptyInputsYieldNoMessages(string? payload)
    {
        Assert.Empty(OtWapGateway.ParseMessagesPayload(payload));
    }
}

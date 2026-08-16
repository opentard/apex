using Tard.Messaging;

namespace Tard.Tests.Messaging;

public class ChatMessageTests
{
    [Fact]
    public void ChatMessage_PropertiesRoundTrip()
    {
        var now = DateTimeOffset.UtcNow;
        var msg = new ChatMessage
        {
            MessageId = "wamid.123",
            FromPhoneNumber = "+14155550001",
            SenderName = "Alice",
            MessageType = "text",
            TextBody = "Hello",
            ReceivedAt = now
        };

        Assert.Equal("wamid.123", msg.MessageId);
        Assert.Equal("+14155550001", msg.FromPhoneNumber);
        Assert.Equal("Alice", msg.SenderName);
        Assert.Equal("text", msg.MessageType);
        Assert.Equal("Hello", msg.TextBody);
        Assert.Null(msg.MediaId);
        Assert.Null(msg.GroupId);
        Assert.Equal(now, msg.ReceivedAt);
    }

    [Fact]
    public void ChatMessage_MediaMessage()
    {
        var msg = new ChatMessage
        {
            MessageId = "wamid.456",
            FromPhoneNumber = "+14155550001",
            SenderName = "Bob",
            MessageType = "image",
            MediaId = "media_123",
            ReceivedAt = DateTimeOffset.UtcNow
        };

        Assert.Equal("image", msg.MessageType);
        Assert.Equal("media_123", msg.MediaId);
        Assert.Null(msg.TextBody);
    }
}

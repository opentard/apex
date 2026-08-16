using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Tard.Agent;
using Tard.Configuration;
using Tard.Messaging;
using Tard.Workers;

namespace Tard.Tests.Workers;

public class MessagePollingWorkerTests
{
    private static MessagePollingWorker CreateWorker(string allowedSenders) =>
        new(
            new Mock<IMessageGateway>().Object,
            new Mock<ITardAgent>().Object,
            Options.Create(new TardOptions { AllowedSenders = allowedSenders }),
            NullLogger<MessagePollingWorker>.Instance);

    [Fact]
    public void ParseAllowedSenders_SplitsTrimsAndIgnoresBlanks()
    {
        var parsed = MessagePollingWorker.ParseAllowedSenders(" +14155550001, +14155550002 ,, ");

        Assert.Equal(2, parsed.Count);
        Assert.Contains("+14155550001", parsed);
        Assert.Contains("+14155550002", parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void IsSenderAllowed_NoAllowlistConfigured_AcceptsEveryone(string allowlist)
    {
        // Unrestricted is the documented default, so it must stay permissive rather than silently
        // dropping every message.
        Assert.True(CreateWorker(allowlist).IsSenderAllowed("+19998887777"));
    }

    [Fact]
    public void IsSenderAllowed_AllowsListedSender()
    {
        Assert.True(CreateWorker("+14155550001,+14155550002").IsSenderAllowed("+14155550002"));
    }

    [Fact]
    public void IsSenderAllowed_RejectsUnlistedSender()
    {
        // The agent can hold memories and (when enabled) run shell commands, so an unlisted sender
        // must never reach it.
        Assert.False(CreateWorker("+14155550001").IsSenderAllowed("+19998887777"));
    }

    [Fact]
    public void IsSenderAllowed_IgnoresSurroundingWhitespaceInConfig()
    {
        Assert.True(CreateWorker(" +14155550001 , +14155550002 ").IsSenderAllowed("+14155550001"));
    }
}

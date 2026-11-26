using Cord.App.Streaming;
using Cord.Protocol;

namespace Cord.App.Tests;

public sealed class StreamEndMessagesTests
{
    [Fact]
    public void RejectedVideoSettingsPointToTheQualitySettings()
    {
        // Reason sent by a PS5 asked for 2160p (docs/protocol/09-stream-connection.md#launch-spec).
        const string reason = "Nagare did not init! AvCap failed to initialize video: [InitResult:-6]";

        var message = StreamEndMessages.ForDisconnect(reason);

        Assert.Contains("lower quality in Settings", message, StringComparison.Ordinal);
        Assert.Contains(reason, message, StringComparison.Ordinal);
    }

    [Fact]
    public void OtherDisconnectReasonsAreShownAsIs()
    {
        Assert.Equal("The console ended the stream: Server shutting down", StreamEndMessages.ForDisconnect("Server shutting down"));
    }

    [Fact]
    public void LostConnectionIsExplained()
    {
        var message = StreamEndMessages.ForEnd($"{Cord.Protocol.Stream.StreamConnection.ConnectionLostReason} Nothing received from the console for 5 s.");

        Assert.StartsWith("The connection to your PS5 was lost.", message, StringComparison.Ordinal);
        Assert.Equal("Connection closed", StreamEndMessages.ForEnd("Connection closed"));
    }

    [Fact]
    public void BusyConsoleAsksToWait()
    {
        var busy = RemotePlayException.FromApplicationReason(0x80108b10, "Session request");

        Assert.Contains("Wait a few seconds", StreamEndMessages.ForFailure(busy), StringComparison.Ordinal);
        Assert.Equal("Connection failed: boom", StreamEndMessages.ForFailure(new IOException("boom")));
    }
}

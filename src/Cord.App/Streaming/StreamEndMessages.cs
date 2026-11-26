using Cord.Protocol;
using Cord.Protocol.Stream;

namespace Cord.App.Streaming;

/// <summary>Turns console disconnect reasons and connection errors into advice a player can act on.</summary>
internal static class StreamEndMessages
{
    /// <summary>For a DISCONNECT message sent by the console.</summary>
    public static string ForDisconnect(string reason) =>
        reason.Contains("AvCap failed to initialize video", StringComparison.OrdinalIgnoreCase)
            ? $"Your PS5 could not start the video with these quality settings. Pick a lower quality in Settings. (Console: {reason})"
            : $"The console ended the stream: {reason}";

    /// <summary>For a stream that ended without a DISCONNECT message (lost or closed connection).</summary>
    public static string ForEnd(string reason) =>
        reason.StartsWith(StreamConnection.ConnectionLostReason, StringComparison.Ordinal)
            ? "The connection to your PS5 was lost. Check that it is still on and connected to the same network as this computer."
            : reason;

    public static string ForFailure(Exception error) => error is RemotePlayException { Error: RemotePlayError.InUse }
        ? "Your PS5 is busy: it is still closing the last session or another device is playing on it. Wait a few seconds and try again."
        : $"Connection failed: {error.Message}";
}

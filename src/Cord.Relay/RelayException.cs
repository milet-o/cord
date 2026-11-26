namespace Cord.Relay;

/// <summary>The relay could not start; the message is meant for the user.</summary>
public sealed class RelayException(string message, Exception innerException) : Exception(message, innerException);

namespace Cord.Protocol;

public enum RemotePlayError
{
    Unknown,
    RegistrationFailed,
    InvalidPsnAccount,
    InUse,
    Crashed,
    VersionMismatch,
    ConsoleNotFound,
    InvalidResponse,
    LoginFailed,
    Disconnected,
}

public sealed class RemotePlayException : Exception
{
    public RemotePlayException(RemotePlayError error, string message, uint? applicationReason = null)
        : base(message)
    {
        Error = error;
        ApplicationReason = applicationReason;
    }

    public RemotePlayException()
        : this(RemotePlayError.Unknown, "Remote Play error.")
    {
    }

    public RemotePlayException(string message)
        : this(RemotePlayError.Unknown, message)
    {
    }

    public RemotePlayException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public RemotePlayError Error { get; }

    public uint? ApplicationReason { get; }

    /// <summary>Maps an RP-Application-Reason code (docs/protocol/04-registration.md).</summary>
    public static RemotePlayException FromApplicationReason(uint? reason, string context)
    {
        var error = reason switch
        {
            0x80108b09 => RemotePlayError.RegistrationFailed,
            0x80108b02 => RemotePlayError.InvalidPsnAccount,
            0x80108b10 => RemotePlayError.InUse,
            0x80108b15 => RemotePlayError.Crashed,
            0x80108b11 => RemotePlayError.VersionMismatch,
            _ => RemotePlayError.Unknown,
        };
        var code = reason is null ? "none" : $"0x{reason:x8}";
        return new RemotePlayException(error, $"{context} failed: {error} (reason {code}).", reason);
    }
}

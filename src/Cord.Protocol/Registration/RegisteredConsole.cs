namespace Cord.Protocol.Registration;

/// <summary>Credentials returned by a successful registration (docs/protocol/04-registration.md).</summary>
public sealed record RegisteredConsole(
    byte[] RegistKey,
    byte[] RpKey,
    int RpKeyType,
    byte[] MacAddress,
    string? Nickname)
{
    public const int RegistKeySize = 16;
    public const int RpKeySize = 16;
    public const int MacAddressSize = 6;
}

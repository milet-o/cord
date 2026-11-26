namespace Cord.Protocol.Stream;

/// <summary>Path MTU and round-trip time measured by senkusha (docs/protocol/10-senkusha.md).</summary>
public sealed record NetworkProbe(int MtuIn, int MtuOut, int RttMilliseconds)
{
    public static readonly NetworkProbe LanDefault = new(1454, 1454, 1);
}

namespace Cord.Protocol.Tests.Support;

internal static class TestBytes
{
    public static byte[] Hex(string hex) => Convert.FromHexString(hex);

    public static byte[] Sequence(int length, Func<int, int> value) =>
        Enumerable.Range(0, length).Select(i => (byte)value(i)).ToArray();
}

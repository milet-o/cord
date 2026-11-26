namespace Cord.WebShare;

/// <summary>Inspects Annex B H.264 and HEVC access units and stream headers without allocating.</summary>
public static class AnnexBVideoInspector
{
    public static bool IsKeyframe(ReadOnlySpan<byte> accessUnit, bool isHevc)
    {
        var offset = 0;
        while (TryNextNal(accessUnit, ref offset, out var header))
        {
            if (isHevc)
            {
                var type = (header >> 1) & 0x3F;
                if (type is 19 or 20 or 21)
                    return true;
            }
            else if ((header & 0x1F) == 5)
            {
                return true;
            }
        }

        return false;
    }

    public static bool ContainsParameterSets(ReadOnlySpan<byte> accessUnit, bool isHevc)
    {
        var hasFirst = false;
        var hasSecond = false;
        var offset = 0;
        while (TryNextNal(accessUnit, ref offset, out var header))
        {
            if (isHevc)
            {
                var type = (header >> 1) & 0x3F;
                if (type == 32)
                    hasFirst = true;
                else if (type == 33)
                    hasSecond = true;
            }
            else
            {
                var type = header & 0x1F;
                if (type == 7)
                    hasFirst = true;
                else if (type == 8)
                    hasSecond = true;
            }

            if (hasFirst && hasSecond)
                return true;
        }

        return false;
    }

    public static string ExtractCodecString(ReadOnlySpan<byte> streamHeader, bool isHevc)
    {
        if (isHevc)
            return "hev1.1.6.L120.90";

        var offset = 0;
        while (TryNextNalStart(streamHeader, ref offset, out var nalStart))
        {
            if (nalStart >= streamHeader.Length)
                break;

            var type = streamHeader[nalStart] & 0x1F;
            if (type == 7 && nalStart + 3 < streamHeader.Length)
            {
                var profile = streamHeader[nalStart + 1];
                var constraints = streamHeader[nalStart + 2];
                var level = streamHeader[nalStart + 3];
                return $"avc1.{profile:x2}{constraints:x2}{level:x2}";
            }
        }

        return "avc1.4d002a";
    }

    private static bool TryNextNal(ReadOnlySpan<byte> data, ref int offset, out byte header)
    {
        if (!TryNextNalStart(data, ref offset, out var nalStart))
        {
            header = 0;
            return false;
        }

        header = data[nalStart];
        return true;
    }

    private static bool TryNextNalStart(ReadOnlySpan<byte> data, ref int offset, out int nalStart)
    {
        while (offset + 3 <= data.Length)
        {
            if (data[offset] == 0 && data[offset + 1] == 0)
            {
                if (data[offset + 2] == 1)
                {
                    var candidate = offset + 3;
                    offset = candidate + 1;
                    if (candidate < data.Length)
                    {
                        nalStart = candidate;
                        return true;
                    }

                    nalStart = 0;
                    return false;
                }

                if (offset + 4 <= data.Length && data[offset + 2] == 0 && data[offset + 3] == 1)
                {
                    var candidate = offset + 4;
                    offset = candidate + 1;
                    if (candidate < data.Length)
                    {
                        nalStart = candidate;
                        return true;
                    }

                    nalStart = 0;
                    return false;
                }
            }

            offset++;
        }

        nalStart = 0;
        return false;
    }
}

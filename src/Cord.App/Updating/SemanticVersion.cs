using System.Globalization;

namespace Cord.App.Updating;

/// <summary>Parses and compares semantic versions (with optional leading 'v' and prerelease tags).</summary>
internal readonly record struct SemanticVersion(int Major, int Minor, int Patch, string? Prerelease) : IComparable<SemanticVersion>
{
    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var span = text.Trim().AsSpan();
        if (span.Length > 0 && span[0] is 'v' or 'V')
            span = span[1..];

        var plus = span.IndexOf('+');
        if (plus >= 0)
            span = span[..plus];

        string? prerelease = null;
        var dash = span.IndexOf('-');
        if (dash >= 0)
        {
            var preSpan = span[(dash + 1)..];
            prerelease = preSpan.IsEmpty ? null : preSpan.ToString();
            span = span[..dash];
        }

        if (span.IsEmpty)
            return false;

        var dot1 = span.IndexOf('.');
        if (dot1 < 0)
            return false;

        var majorSpan = span[..dot1];
        var rest = span[(dot1 + 1)..];
        var dot2 = rest.IndexOf('.');
        ReadOnlySpan<char> minorSpan;
        ReadOnlySpan<char> patchSpan;
        if (dot2 < 0)
        {
            minorSpan = rest;
            patchSpan = "0";
        }
        else
        {
            minorSpan = rest[..dot2];
            patchSpan = rest[(dot2 + 1)..];
            if (patchSpan.IndexOf('.') >= 0)
                return false;
        }

        if (!int.TryParse(majorSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(minorSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(patchSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch, prerelease);
        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        var cmp = Major.CompareTo(other.Major);
        if (cmp != 0)
            return cmp;
        cmp = Minor.CompareTo(other.Minor);
        if (cmp != 0)
            return cmp;
        cmp = Patch.CompareTo(other.Patch);
        if (cmp != 0)
            return cmp;

        var selfPre = !string.IsNullOrEmpty(Prerelease);
        var otherPre = !string.IsNullOrEmpty(other.Prerelease);
        if (!selfPre && !otherPre)
            return 0;
        if (!selfPre)
            return 1;
        if (!otherPre)
            return -1;

        var leftParts = Prerelease!.Split('.');
        var rightParts = other.Prerelease!.Split('.');
        var count = Math.Min(leftParts.Length, rightParts.Length);
        for (var i = 0; i < count; i++)
        {
            var leftIsNum = int.TryParse(leftParts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var leftNum);
            var rightIsNum = int.TryParse(rightParts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var rightNum);
            if (leftIsNum && rightIsNum)
            {
                var numCmp = leftNum.CompareTo(rightNum);
                if (numCmp != 0)
                    return numCmp;
            }
            else if (leftIsNum != rightIsNum)
            {
                return leftIsNum ? -1 : 1;
            }
            else
            {
                var strCmp = string.Compare(leftParts[i], rightParts[i], StringComparison.OrdinalIgnoreCase);
                if (strCmp != 0)
                    return strCmp;
            }
        }

        return leftParts.Length.CompareTo(rightParts.Length);
    }

    public static bool IsNewer(string? candidate, string? current) =>
        TryParse(candidate, out var c) && TryParse(current, out var cur) && c.CompareTo(cur) > 0;
}

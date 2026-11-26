namespace Cord.Protocol.Av;

/// <summary>
/// Cauchy Reed–Solomon erasure code used for video frames
/// (docs/protocol/11-video.md#fec-recovery).
/// </summary>
public static class CauchyFec
{
    /// <summary>Coefficient of source unit <paramref name="j"/> in coding unit <paramref name="i"/>.</summary>
    internal static byte Coefficient(int i, int j, int codingUnits) =>
        Gf256.Inverse((byte)(i ^ (codingUnits + j)));

    /// <summary>Computes the <paramref name="m"/> coding units from <paramref name="k"/> source units.</summary>
    public static void Encode(IReadOnlyList<Memory<byte>> units, int k, int m)
    {
        for (var i = 0; i < m; i++)
        {
            var coding = units[k + i].Span;
            coding.Clear();
            for (var j = 0; j < k; j++)
                Gf256.MultiplyAdd(coding, units[j].Span, Coefficient(i, j, m));
        }
    }

    /// <summary>
    /// Restores missing source units in place. <paramref name="present"/> tells which of the
    /// <c>k + m</c> units were received. Returns false when fewer than k units are present.
    /// </summary>
    public static bool Decode(IReadOnlyList<Memory<byte>> units, IReadOnlyList<bool> present, int k, int m)
    {
        var missing = Enumerable.Range(0, k).Where(j => !present[j]).ToList();
        if (missing.Count == 0)
            return true;

        var available = Enumerable.Range(0, k + m).Where(u => present[u]).Take(k).ToList();
        if (available.Count < k)
            return false;

        var matrix = new byte[k, k];
        for (var row = 0; row < k; row++)
        {
            var unit = available[row];
            for (var column = 0; column < k; column++)
            {
                matrix[row, column] = unit < k
                    ? (byte)(unit == column ? 1 : 0)
                    : Coefficient(unit - k, column, m);
            }
        }

        var inverse = Invert(matrix, k);
        var unitSize = units[0].Length;
        var recovered = missing.ToDictionary(j => j, _ => new byte[unitSize]);
        foreach (var j in missing)
        {
            for (var row = 0; row < k; row++)
                Gf256.MultiplyAdd(recovered[j], units[available[row]].Span, inverse[j, row]);
        }

        foreach (var (j, data) in recovered)
            data.CopyTo(units[j]);
        return true;
    }

    private static byte[,] Invert(byte[,] matrix, int size)
    {
        var a = (byte[,])matrix.Clone();
        var inverse = new byte[size, size];
        for (var i = 0; i < size; i++)
            inverse[i, i] = 1;

        for (var column = 0; column < size; column++)
        {
            var pivot = column;
            while (a[pivot, column] == 0)
            {
                pivot++;
                if (pivot == size)
                    throw new InvalidOperationException("FEC matrix is singular.");
            }

            if (pivot != column)
            {
                SwapRows(a, pivot, column, size);
                SwapRows(inverse, pivot, column, size);
            }

            var scale = Gf256.Inverse(a[column, column]);
            for (var c = 0; c < size; c++)
            {
                a[column, c] = Gf256.Multiply(a[column, c], scale);
                inverse[column, c] = Gf256.Multiply(inverse[column, c], scale);
            }

            for (var row = 0; row < size; row++)
            {
                var factor = a[row, column];
                if (row == column || factor == 0)
                    continue;
                for (var c = 0; c < size; c++)
                {
                    a[row, c] ^= Gf256.Multiply(factor, a[column, c]);
                    inverse[row, c] ^= Gf256.Multiply(factor, inverse[column, c]);
                }
            }
        }

        return inverse;
    }

    private static void SwapRows(byte[,] matrix, int first, int second, int size)
    {
        for (var c = 0; c < size; c++)
            (matrix[first, c], matrix[second, c]) = (matrix[second, c], matrix[first, c]);
    }
}

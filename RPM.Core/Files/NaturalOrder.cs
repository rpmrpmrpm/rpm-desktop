namespace RPM.Core.Files;

/// <summary>
/// Compares file names the way a person reads them: the digit runs inside a name are compared as
/// numbers, so "Track 2" comes before "Track 10" rather than after it.
///
/// <para>Plain string ordering puts "10" before "2" because '1' is less than '2'. That turns a folder
/// of an album's tracks into the order 1, 10, 11, 12, 2, 3. This matters wherever file order stands in
/// for running order. A rip with no tags has nothing else to go on. If such a folder is paired with a
/// release listing in the wrong order, every file gets a neighbour's name.</para>
/// </summary>
public sealed class NaturalOrder : IComparer<string?>
{
    /// <summary>The shared instance. Stateless, so one is enough.</summary>
    public static readonly NaturalOrder Comparer = new();

    private NaturalOrder() { }

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                // Whole digit runs against each other, not digit by digit. Leading zeros are skipped,
                // so "07" and "7" are the same number. Both spellings are common in one folder.
                var (xi, xLen) = Digits(x, i);
                var (yj, yLen) = Digits(y, j);
                if (xLen != yLen)
                    return xLen - yLen;      // more digits, once zeros are gone, is a bigger number

                for (var k = 0; k < xLen; k++)
                {
                    if (x[xi + k] != y[yj + k])
                        return x[xi + k] - y[yj + k];
                }

                i = xi + xLen;
                j = yj + yLen;
                continue;
            }

            var c = char.ToLowerInvariant(x[i]).CompareTo(char.ToLowerInvariant(y[j]));
            if (c != 0)
                return c;
            i++;
            j++;
        }

        // One ran out: the shorter name is the earlier one ("Track 1" before "Track 1 (reprise)").
        return (x.Length - i) - (y.Length - j);
    }

    /// <summary>Where the significant digits of the run at <paramref name="from"/> start, and how many
    /// there are. Leading zeros are dropped, but the last digit is always kept, so "000" is one zero and
    /// not none.</summary>
    private static (int Start, int Length) Digits(string s, int from)
    {
        var end = from;
        while (end < s.Length && char.IsDigit(s[end]))
            end++;

        var start = from;
        while (start < end - 1 && s[start] == '0')
            start++;

        return (start, end - start);
    }
}

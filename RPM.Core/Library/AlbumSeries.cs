namespace RPM.Core.Library;

/// <summary>
/// The series a record is one volume of: "The Complete Mozart Edition", volume 43.
///
/// <para><b>The listener's own answer, kept in the library and not in the files.</b> No tag means this
/// in every player. GROUPING is a per-track field that half the taggers fill with the work and the
/// other half with the box set, and a series name in the files would arrive as different text on every
/// volume. It is said once per record, in the album editor or in the import queue, and it stays
/// through every rescan and retag, like the classical mark.</para>
///
/// <para>The volume is text, not a number. Series number their volumes "43", "IV", "3a" and
/// "1–2", and the listener's own spelling is what is shown. <see cref="Order"/> is what they sort
/// by.</para>
/// </summary>
/// <param name="Name">The series' name. Never empty: a record with no name is in no series.</param>
/// <param name="Volume">Which volume this record is, or null where the series is not numbered.</param>
public sealed record AlbumSeries(string Name, string? Volume = null)
{
    /// <summary>The series as a person typed it, trimmed, or null when the name is empty. An empty
    /// volume is no volume.</summary>
    public static AlbumSeries? Of(string? name, string? volume)
        => name?.Trim() is { Length: > 0 } named
            ? new AlbumSeries(named, volume?.Trim() is { Length: > 0 } v ? v : null)
            : null;

    /// <summary>Where this volume sorts among the others: by the number it starts with, so volume 9
    /// comes before volume 43, and then by its spelling. A volume with no number sorts last.</summary>
    public (long Number, string Text) Order
    {
        get
        {
            var text = Volume ?? "";
            var digits = new string([.. text.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit)]);
            return (long.TryParse(digits, out var n) ? n : long.MaxValue, text);
        }
    }
}

using RPM.Desktop.Presentation.Dialogs;
using RPM.Desktop.Presentation.Main.LeftPane.Models;

namespace RPM.Desktop.Presentation.Import;

/// <summary>What a set of rows says about one field: the same thing, or several things.</summary>
public static class TagPanelValues
{
    /// <summary>
    /// Reads <paramref name="field"/> off every row.
    ///
    /// <para><c>AllSame</c> decides whether the panel draws a box or a list. It follows the mass editor's
    /// convention, so the two editors do not differ. Where the rows disagree, the control starts empty,
    /// shows the "multiple values" placeholder, and offers what each row says. A field that a person
    /// meets in two windows should not need learning twice.</para>
    /// </summary>
    public static (bool AllSame, string CommonValue, IReadOnlyList<string> Distinct) Of(
        IReadOnlyList<TrackRow> rows, TagField field)
    {
        var values = rows.Select(r => field.Get(r) ?? "").ToList();
        var distinct = values.Distinct(StringComparer.Ordinal).ToList();

        return distinct.Count <= 1
            ? (true, distinct.Count == 0 ? "" : distinct[0], distinct)
            : (false, "", distinct);
    }

    /// <summary>
    /// The one picture every row carries, or null where the rows carry different pictures or none.
    ///
    /// <para>Pictures are compared by BYTES. Each row is loaded from the store on its own, so one cover
    /// chosen for a record comes back as a separate <c>byte[]</c> per track, with identical bytes. Compared
    /// by reference, a record would never agree with itself. The well would go empty as soon as more than
    /// one file was selected, and show the cover again when the selection came back to one.</para>
    /// </summary>
    public static byte[]? AgreedPicture(IReadOnlyList<byte[]?> pictures)
    {
        if (pictures.Count == 0 || pictures[0] is not { Length: > 0 } first)
            return null;

        for (int i = 1; i < pictures.Count; i++)
            if (pictures[i] is not { } other || !first.AsSpan().SequenceEqual(other))
                return null;

        return first;
    }
}

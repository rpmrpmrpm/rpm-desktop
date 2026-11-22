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

    /// <summary>
    /// What one box should show after its rows were read again from the store under it, or null to leave the
    /// box as it is.
    ///
    /// <para>The rows are read again whenever the queue changes, and the catalogue fill changes it on its own
    /// while somebody is typing. A box the user has changed keeps what they put in it, applied or not. So does
    /// the box they are typing in, even before it counts as changed. Every other box shows what the rows say
    /// now, so a genre the fill found appears in an empty genre box. Where the rows now disagree the box is
    /// left alone: it cannot show several values, and it would write nothing anyway.</para>
    /// </summary>
    /// <param name="touched">The box shows something other than what it was filled with.</param>
    /// <param name="focused">The keyboard is in the box.</param>
    /// <param name="shows">What the box would write now.</param>
    /// <param name="allSame">Whether the rows agree on the field now.</param>
    /// <param name="common">What they agree on.</param>
    public static string? Refreshed(bool touched, bool focused, string shows, bool allSame, string common)
    {
        if (touched || focused || !allSame)
            return null;
        return string.Equals(shows, common, StringComparison.Ordinal) ? null : common;
    }

    /// <summary>
    /// The ticked rows as one comparable value: their store ids, in number order.
    ///
    /// <para>In number order, not in the order they were read. A rebuild of the tree can read the same rows in
    /// another order, and that is still the same selection. Read as a new selection, it would throw away what
    /// the panel holds for it.</para>
    /// </summary>
    public static string SelectionKey(IEnumerable<long> queueIds)
        => string.Join(",", queueIds.Order());
}

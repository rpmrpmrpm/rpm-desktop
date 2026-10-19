namespace RPM.Desktop.Presentation.Main.Library;

/// <summary>
/// The tabs of the groove, named once.
///
/// <para>The groove is the social part of RPM: everything listeners do around a record. For now that is
/// reviews and comments, which are tabs rather than two stacked boxes, because the list will grow.
/// Planned tabs are people (who a listener follows) and the records and artists a listener is watching.
/// Each one is a tab in this same strip, not another box under the last one.</para>
///
/// <para>The ids are named here, like <see cref="Dialogs.MetadataTabs"/>. A panel is registered under its
/// id, and a remembered choice is stored as the id. A string typed twice can drift, and then a tab opens
/// on one screen and not on another.</para>
/// </summary>
internal static class GrooveTabs
{
    /// <summary>What people wrote about the record: a rating in stars, and prose under it.</summary>
    public const string Reviews = "reviews";

    /// <summary>What they said in passing. Shorter, with no rating.</summary>
    public const string Comments = "comments";
}

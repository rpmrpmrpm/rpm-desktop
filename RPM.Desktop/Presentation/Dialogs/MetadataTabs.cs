namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>
/// The tabs a metadata editor is divided into, named once.
///
/// <para>The three windows (the per-file editor, the album editor and the mass edit) show overlapping
/// subsets of one field catalogue (see <see cref="TagFields"/>). A field must be found in the same
/// place in each window. Naming the tabs here keeps "the composer is under Credits" true in all three,
/// not only in one of them.</para>
///
/// <para>Not every window shows every tab. A mass edit over several unrelated files has no artwork tab,
/// and only the per-file editor has a lyrics tab. <see cref="Controls.RailTabStrip.Hide"/> removes the
/// tabs that a window has nothing to put under.</para>
/// </summary>
internal static class MetadataTabs
{
    /// <summary>What the file calls itself: names, numbers, the record it came off.</summary>
    public const string Basic = "basic";

    /// <summary>The people on it, and who published it.</summary>
    public const string Credits = "credits";

    /// <summary>The composition it performs, how it sorts, which pressing it is, and its ids.</summary>
    public const string Work = "work";

    /// <summary>
    /// What the app measured from the audio itself: the tempo and the key, together with the readings
    /// they were worked out from, and the loudness of the record.
    ///
    /// <para>Per-file only, with its own tab. The two editable fields are ordinary tags. The readings a
    /// listener needs in order to judge them are measurements, and they are kept apart from the people
    /// and credits.</para>
    /// </summary>
    public const string Sound = "sound";

    /// <summary>The words. Per-file only, because a selection has no one set of lyrics.</summary>
    public const string Lyrics = "lyrics";

    /// <summary>The sleeve, and the label printed on the disc.</summary>
    public const string Artwork = "artwork";

    /// <summary>
    /// What the library thinks about this record, as against what its files say: whether it is
    /// classical, and whether it is levelled with the rest of the collection.
    ///
    /// <para>Album editor only. Both answers are the app's own opinion, and neither is written into a
    /// file. That is why they are not on a tab full of tags.</para>
    /// </summary>
    public const string Settings = "settings";
}

namespace RPM.Desktop.Presentation.Main.Library;

/// <summary>
/// What the album page offers about the record's pressing link. The offer depends on whose shelf the
/// record is on, as well as on whether it is linked.
///
/// <para>There are three answers, computed in one place, because one rule decides all of them and the
/// page asks it from four places. A borrowed shelf is read only (see <c>LibraryService.Shelf</c>). The
/// link on a record there was made by the owner of the disk. Nothing at this desk can make or change it.
/// So the key that links a pressing is not offered on a borrowed shelf, and an unlinked record says who
/// has not linked it, rather than inviting the reader to do so.</para>
///
/// <para>The groove goes with the link. The groove is RPM's own wall for the record, and it is reached
/// through the link. On a borrowed shelf, an unlinked record has no groove, because the reader cannot
/// make the link. Once linked, the groove is offered whoever's disk the copy is on. A review is about the
/// album, and the reader writes it with their own account.</para>
/// </summary>
/// <param name="Groove">Whether the groove is shown on the page at all.</param>
/// <param name="LinkKey">Whether the key that links, or re-links, the pressing is offered.</param>
/// <param name="NameTheLender">Whether the "not linked" line names the owner of the record, instead of
/// stating only the fact.</param>
internal readonly record struct LinkOffer(bool Groove, bool LinkKey, bool NameTheLender)
{
    /// <summary>
    /// The offer for one record: whether it is linked to a pressing, and whether the shelf it stands on
    /// was borrowed from somebody else.
    /// </summary>
    /// <remarks>Both values are required, and neither has a default. When every shelf belonged to the
    /// reader, "is it linked" was the only question. A default for the second value would let the
    /// borrowed case answer as if the shelf were owned. That would offer a link key that writes to a
    /// mirror this machine does not own.</remarks>
    public static LinkOffer For(bool linked, bool borrowed) => new(
        Groove: linked || !borrowed,
        LinkKey: !borrowed,
        NameTheLender: borrowed);
}

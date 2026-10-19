using RPM.Fingerprint.Catalog;

namespace RPM.Core.Metadata.Providers;

/// <summary>
/// Whether the two halves of a record's papers name the same act, and which one to believe when they
/// do not.
///
/// <para><b>The papers answer two questions, and they can answer them about different people.</b>
/// <see cref="IRpmMetadataApi.GetPapersAsync"/> returns an act and a record. The record carries its own
/// <see cref="CatalogAlbum.ArtistId"/>. The two are found in different ways. The record is matched on
/// its title, the act's name and the number of tracks. The act is matched on its name only. A name is
/// the weakest key in any catalogue.</para>
///
/// <para><b>What this is for, in one case.</b> A search for <i>Maid in England</i> by Divine returns the
/// right record, the drag performer's 1988 album, with the right article about it. Beside it is an
/// artist card for DiVine, a Dutch act with no connection to it. Both halves are in the same answer.
/// The record points straight at <c>9572db59-da29-4f9d-bc00-0287ee3386e9</c>, and nothing checked
/// it.</para>
///
/// <para><b>The record wins, and not because it is more likely to be right in general.</b> It wins
/// because it was matched on more: the act's name, plus a title, plus a track count. If the record does
/// not name an artist at all, or names Various Artists, nothing better is available and the act stays as
/// it was.</para>
/// </summary>
public static class PapersAgree
{
    /// <summary>
    /// The act's id as the RECORD names it, where that should be believed over the one the papers
    /// picked. Null when the two agree, when the record names nobody, or when what it names is not an
    /// act at all.
    /// </summary>
    /// <remarks>
    /// Null is the most common answer. The two halves agree about nearly every record, and the caller
    /// does no work when they do.
    ///
    /// <para><b>Various Artists is refused.</b> It is the one case where the record's credit is certainly
    /// not the act being asked about. A compilation's release group is credited to Various Artists.
    /// Believing it would replace every act on every compilation with an unrelated card. That is the
    /// same fault this method fixes, pointing the other way.</para>
    ///
    /// <para><b>An act recognised by name alone has no id.</b> Then the record's id is better than no id.
    /// There is no second opinion to weigh, only an answer or no answer.</para>
    /// </remarks>
    public static string? ArtistTheRecordNames(CatalogPapers? papers)
    {
        if (papers?.Album is not { } album)
            return null;

        if (album.ArtistId is not { Length: > 0 } named || string.IsNullOrWhiteSpace(named))
            return null;

        // A compilation credits its release group to Various Artists. That is a true fact about the
        // record and a useless one about the act, so it is never preferred.
        if (ArtistCredit.IsVariousArtists(album.Artist))
            return null;

        // They agree. This is the usual case, and it costs nothing.
        if (papers.Artist?.Id is { Length: > 0 } picked
            && string.Equals(picked, named, StringComparison.OrdinalIgnoreCase))
            return null;

        return named;
    }

    /// <summary>
    /// The papers with <paramref name="truer"/> in place of the act they came with.
    /// </summary>
    /// <remarks>
    /// The record is not changed. It is the half that was matched on more than a name, and it is the
    /// source of the act that is put in its place.
    /// </remarks>
    public static CatalogPapers WithArtist(CatalogPapers papers, CatalogArtist? truer)
    {
        ArgumentNullException.ThrowIfNull(papers);

        return truer is null || truer.IsEmpty ? papers : papers with { Artist = truer };
    }
}

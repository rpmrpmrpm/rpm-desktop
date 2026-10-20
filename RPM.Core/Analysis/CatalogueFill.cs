using System.Text.RegularExpressions;
using RPM.Core.Metadata;
using RPM.Core.Metadata.Providers;
using RPM.Core.Models;

namespace RPM.Core.Analysis;

/// <summary>
/// The genre, the style and the year that a queued record lacks, filled in from the catalogue without anybody
/// asking.
///
/// <para><b>Why it exists.</b> The genre pages, the mood lists and the song radio all read a record's genre and
/// style, and they read them as the app's own shelves and styles (<see cref="Genres"/>, <see cref="Subgenres"/>).
/// A record with no genre, or with one the lists do not know, is invisible to all of them. Plenty of people
/// never open a tag editor, so the lookup that would fix it is never pressed. This is that lookup, made once per
/// record when the music is added, and kept to the fields the features need.</para>
///
/// <para><b>Only blanks.</b> A value the file or a person gave that is on the app's lists is never replaced. The
/// one value that is replaced is a genre the lists cannot read at all ("Albums I Own", "Misc"), because no
/// feature can use it and the catalogue's answer is strictly more useful. A genre the lists CAN read but that
/// is not spelled as a shelf ("hip hop") is translated to its shelf, not replaced. Its writer meant that shelf.
/// What is filled goes onto the WANTED side of the queue row, where the user sees it before anything is
/// imported, and nothing is written to a file until they import.</para>
///
/// <para><b>Only a sure match.</b> A wrong release would put a wrong genre on a whole record, silently, which is
/// worse than leaving it blank. So a release is used only when its title and artist agree with the record's
/// once case, accents, punctuation and edition notes are set aside, and its track count is close to the
/// number of files. A file that names its MusicBrainz release is taken at its word, because that is the most
/// exact answer there is.</para>
/// </summary>
public static partial class CatalogueFill
{
    /// <summary>One queued record, as the lookup asks about it.</summary>
    /// <param name="Artist">The record's artist: the album artist its files agree on, or their artist.</param>
    /// <param name="Title">The album title, without a "CD 1" suffix.</param>
    /// <param name="TrackCount">How many files the queue holds for it.</param>
    /// <param name="ReleaseId">The MusicBrainz release every file that names one names, or null.</param>
    public sealed record Album(string Artist, string Title, int TrackCount, string? ReleaseId);

    /// <summary>Whether a file has anything this pass could fill: no genre, a genre the lists do not spell as a
    /// shelf, no year, or no style on a shelf that has styles to give. A record whose files all say everything
    /// is not looked up at all, and neither is one a person filed under <see cref="Genres.Unknown"/>, which has
    /// no styles to fill.</summary>
    public static bool Wants(TrackInfo wanted)
        => Genres.Listed(wanted.Genre) is not { } shelf
           || wanted.Year == 0
           || (string.IsNullOrWhiteSpace(wanted.Subgenre) && Subgenres.Under(shelf).Count > 0);

    /// <summary>
    /// The record a set of queued files is, or null when there is nothing to ask about: no album title, no
    /// artist, or no file that wants anything (<see cref="Wants"/>).
    /// </summary>
    public static Album? AlbumOf(IReadOnlyList<TrackInfo> files)
    {
        if (files.Count == 0 || !files.Any(Wants))
            return null;

        var title = MostCommon(files.Select(f => f.Album is { } a ? AlbumResolver.NormalizeAlbum(a) : null));
        var artist = MostCommon(files.Select(f => f.AlbumArtist)) ?? MostCommon(files.Select(f => f.Artist));
        if (title is null || artist is null)
            return null;

        // The release id counts only when every file that carries one carries the same one. Two ids in one
        // record are two pressings, and picking either would be a guess.
        var ids = files.Select(f => f.MusicBrainzReleaseId?.Trim())
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new Album(artist, title, files.Count, ids.Count == 1 ? ids[0] : null);

        static string? MostCommon(IEnumerable<string?> values)
            => values.Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!.Trim())
                .GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault();
    }

    /// <summary>A trailing note in brackets: "(Deluxe Edition)", "[Remastered 2011]". A catalogue and a file
    /// disagree about these more than about anything else in a title, and they never change which record it
    /// is.</summary>
    [GeneratedRegex(@"\s*[\(\[][^\(\)\[\]]*[\)\]]\s*$")]
    private static partial Regex TrailingNote();

    /// <summary>A title or a name folded for comparison: case, accents, punctuation, a leading "the" and "&amp;"
    /// against "and" make no difference (see <see cref="SimilarAlbums.Fold"/>).</summary>
    private static string Fold(string? text)
        => SimilarAlbums.Fold(text?.Replace("&", " and ", StringComparison.Ordinal));

    /// <summary>Whether two album titles name the same record: equal once folded, or equal once a trailing note
    /// is taken off either of them.</summary>
    internal static bool SameTitle(string? ours, string? theirs)
    {
        if (string.IsNullOrWhiteSpace(ours) || string.IsNullOrWhiteSpace(theirs))
            return false;

        var a = Fold(AlbumResolver.NormalizeAlbum(ours));
        var b = Fold(theirs);
        if (a.Length > 0 && a == b)
            return true;

        var bareA = Fold(TrailingNote().Replace(AlbumResolver.NormalizeAlbum(ours), ""));
        var bareB = Fold(TrailingNote().Replace(theirs, ""));
        return bareA.Length > 0 && (bareA == b || a == bareB || bareA == bareB);
    }

    /// <summary>Whether two credits name the same act, folded the same way as a title.</summary>
    internal static bool SameArtist(string? ours, string? theirs)
        => Fold(ours) is { Length: > 0 } a && a == Fold(theirs);

    /// <summary>
    /// Whether a release's track count is close enough to the files in hand. Within two tracks, or within a
    /// sixth of the longer list on a long record. A bonus track or a hidden one is not a different record; a
    /// four-track single against a twelve-track album is. Zero, which is a catalogue that did not say, is not
    /// held against it here; the listing is checked again once it is fetched.
    /// </summary>
    internal static bool CloseCount(int files, int release)
    {
        if (release <= 0 || files <= 0)
            return release <= 0;
        var slack = Math.Max(2, Math.Max(files, release) / 6);
        return Math.Abs(files - release) <= slack;
    }

    /// <summary>Whether a search result is a release this record surely is.</summary>
    public static bool Confident(Album album, ReleaseSearchResult found)
        => SameTitle(album.Title, found.Title)
           && SameArtist(album.Artist, found.Artist)
           && CloseCount(album.TrackCount, found.TrackCount);

    /// <summary>Whether a fetched listing is a release this record surely is. Asked of the listing as well as of
    /// the search row, because only the listing counts its tracks for certain.</summary>
    public static bool Confident(Album album, ReleaseDetail listing)
        => SameTitle(album.Title, listing.Title)
           && SameArtist(album.Artist, listing.Artist)
           && CloseCount(album.TrackCount, listing.Tracks.Count);

    /// <summary>
    /// The release a record surely is, fetched, or null when the catalogue has none it is sure of.
    /// <see cref="Asked{T}.Unanswered"/> when the catalogue could not be asked, which says nothing about this
    /// record and is not to be remembered as "no match".
    ///
    /// <para>A file's own release id is fetched first and believed without the title check: whoever wrote it
    /// chose that pressing. Otherwise one search by artist and title, and the most relevant result that passes
    /// <see cref="Confident(Album, ReleaseSearchResult)"/>, with an exact track count preferred, is fetched and
    /// checked again. Two requests at most, whatever the outcome.</para>
    /// </summary>
    public static async Task<Asked<ReleaseDetail?>> FindAsync(
        IMetadataProvider catalogue, Album album, CancellationToken ct = default)
    {
        if (album.ReleaseId is { } id)
        {
            if (!(await catalogue.GetReleaseAsync(id, ct)).Answers(out var named, out var why))
                return Asked.Failed<ReleaseDetail?>(why);
            if (named is not null)
                return Asked.Answer<ReleaseDetail?>(named);
            // The id names nothing the catalogue knows. A stale or mistyped tag; the search may still find it.
        }

        if (!(await catalogue.SearchAsync(new ReleaseQuery(album.Title, album.Artist, Limit: 10), ct))
                .Answers(out var results, out var searchWhy))
            return Asked.Failed<ReleaseDetail?>(searchWhy);

        var best = results
            .Where(r => Confident(album, r))
            .OrderByDescending(r => r.TrackCount == album.TrackCount)
            .ThenByDescending(r => r.Relevance)
            .FirstOrDefault();
        if (best is null)
            return Asked.Answer<ReleaseDetail?>(null);

        if (!(await catalogue.GetReleaseAsync(best.Ref.Id, ct)).Answers(out var listing, out var fetchWhy))
            return Asked.Failed<ReleaseDetail?>(fetchWhy);

        return Asked.Answer(listing is not null && Confident(album, listing) ? listing : null);
    }

    /// <summary>
    /// One file's wanted tags with the blanks filled from a release, or the same instance when nothing changed.
    ///
    /// <list type="bullet">
    /// <item><b>Genre.</b> A shelf as the list spells it is kept, in the list's own spelling. A genre the lists
    /// can read is translated to its shelf, and its style is kept where the style box is empty ("Deep House" is
    /// House and Deep House). A blank genre, or one the lists cannot read, takes the release's shelf. A genre the
    /// lists cannot read is kept when the release has no shelf either; it is not blanked.</item>
    /// <item><b>Style.</b> Only when blank, and only a style under the genre the file ends up with: the release's
    /// own style when it hangs off that shelf, otherwise the first of the catalogue's words that is a style of it
    /// (<see cref="Genres.StyleUnder"/>). A style from another shelf is never paired with this one.</item>
    /// <item><b>Year.</b> Only when 0: when the record first came out, which is what the year means everywhere
    /// in the app, and the pressing's year only where the catalogue does not know the first. The original year
    /// is filled when blank too.</item>
    /// </list>
    ///
    /// <para>A file this changes is marked <see cref="TrackInfo.TagsEdited"/>, as <see cref="AlbumArtistFill"/>
    /// marks its own: that is what carries a filled field into the file at import, rather than only into the
    /// library's own row.</para>
    /// </summary>
    public static TrackInfo Fill(TrackInfo wanted, ReleaseDetail release)
    {
        var genre = Genres.Listed(wanted.Genre);
        var subgenre = string.IsNullOrWhiteSpace(wanted.Subgenre) ? null : wanted.Subgenre;

        if (genre is null && Genres.Read(wanted.Genre) is { IsKnown: true } read)
        {
            genre = read.Genre;
            if (subgenre is null && read.HasSubgenre)
                subgenre = read.Subgenre;
        }

        if (genre is null && Genres.Listed(release.Genre) is { } theirs)
            genre = theirs;

        if (subgenre is null && genre is not null)
        {
            subgenre = release.Subgenre is { Length: > 0 } style
                       && Subgenres.Under(genre).Any(s => s.Name.Equals(style, StringComparison.OrdinalIgnoreCase))
                ? style
                : Genres.StyleUnder(genre, release.Genres);
        }

        var year = wanted.Year != 0 ? wanted.Year
            : release.OriginalYear != 0 ? release.OriginalYear
            : release.Year;
        var originalYear = wanted.OriginalYear != 0 ? wanted.OriginalYear : release.OriginalYear;

        // A genre nothing could read, with no shelf from the release either, stays exactly as written. So does a
        // blank style that stayed blank, in whatever form of blank the file had, so it does not count as a change.
        var finalGenre = genre ?? wanted.Genre;
        subgenre ??= wanted.Subgenre;

        if (string.Equals(finalGenre, wanted.Genre, StringComparison.Ordinal)
            && string.Equals(subgenre, wanted.Subgenre, StringComparison.Ordinal)
            && year == wanted.Year
            && originalYear == wanted.OriginalYear)
            return wanted;

        return wanted with
        {
            Genre = finalGenre,
            Subgenre = subgenre,
            Year = year,
            OriginalYear = originalYear,
            TagsEdited = true,
        };
    }
}

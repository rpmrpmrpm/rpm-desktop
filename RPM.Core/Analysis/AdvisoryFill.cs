using System.Text.RegularExpressions;
using RPM.Core.Library;
using RPM.Core.Metadata;
using RPM.Core.Metadata.Providers;
using RPM.Core.Models;

namespace RPM.Core.Analysis;

/// <summary>
/// The explicit-lyrics flag that a record's songs lack, taken from the release the catalogue matched the record
/// to.
///
/// <para><b>Where the answer comes from.</b> The rpm API's release listing carries a flag per track. The API
/// takes it from the iTunes store, and only for a store album that has the same title, artist and track count as
/// the release (see <c>ITunesAdvisoryClient</c>). The app does not ask a store itself. A song the listing has
/// no flag for stays as it is.</para>
///
/// <para><b>How a song is matched to a track of the listing.</b> The titles must be the same once case, accents,
/// punctuation, a "feat." credit and a trailing note in brackets are set aside. Where both lengths are known they
/// must be within <see cref="NearSeconds"/>. Where several tracks have the title (an intro, a reprise), the track
/// in the same place on the record is taken; without one, they must all agree.</para>
///
/// <para><b>What is changed.</b> Only a song with no flag, or with Clean, and only ever to Explicit. Many
/// downloaders write Clean when they mean "not explicit", so Clean is changed too, but only on a strong match:
/// the same place on the record, both lengths known and within <see cref="StrongSeconds"/>, and a listing with
/// exactly as many tracks as the record has songs. Explicit is never changed, and nothing is ever made Clean:
/// the store's "clean" means "this is the edited version", which a near match cannot tell.</para>
///
/// <para>An album is explicit when any of its songs is, so filling the songs fills the album.</para>
/// </summary>
public static partial class AdvisoryFill
{
    /// <summary>How far apart two lengths may be for a song with no flag.</summary>
    public const double NearSeconds = 8;

    /// <summary>How far apart two lengths may be before a song marked Clean is changed.</summary>
    public const double StrongSeconds = 3;

    /// <summary>One song of a record, as the match reads it.</summary>
    /// <param name="Seconds">The length, or 0 when it is not known.</param>
    /// <param name="Disc">The disc number, or 0 when the song names none (read as disc 1).</param>
    /// <param name="Track">The track number on its disc, or 0 when it has none.</param>
    public sealed record Song(string? Title, double Seconds, uint Disc, uint Track, ContentAdvisory Advisory);

    /// <summary>Whether a song's flag is one this fill may change: none, or Clean.</summary>
    public static bool Wants(ContentAdvisory advisory)
        => advisory is ContentAdvisory.Unknown or ContentAdvisory.Clean;

    /// <summary>
    /// The flag a song should have, or null to leave it as it is.
    /// </summary>
    /// <param name="songsOnRecord">How many songs the record has. A Clean song is changed only when the listing
    /// has exactly this many tracks.</param>
    public static ContentAdvisory? Decide(Song song, ReleaseDetail release, int songsOnRecord)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(release);
        if (!Wants(song.Advisory))
            return null;

        var title = TitleKey(song.Title);
        if (title.Length == 0)
            return null;

        var named = release.Tracks
            .Where(t => TitleKey(t.Title) == title && Near(song.Seconds, t.LengthSeconds, NearSeconds))
            .ToList();
        if (named.Count == 0)
            return null;

        var here = named.Where(t => SamePlace(song, t, release)).ToList();
        ReleaseTrackInfo? match = here.Count == 1 ? here[0]
            : named.Count == 1 ? named[0]
            : null;

        if (song.Advisory == ContentAdvisory.Clean)
        {
            // Strong only: the same place, both lengths known and close, and the same number of tracks.
            var strong = match is not null
                         && here.Count == 1
                         && release.Tracks.Count == songsOnRecord
                         && song.Seconds > 0 && match.LengthSeconds is > 0
                         && Near(song.Seconds, match.LengthSeconds, StrongSeconds);
            return strong && match!.Advisory == ContentAdvisory.Explicit ? ContentAdvisory.Explicit : null;
        }

        if (match is not null)
            return match.Advisory == ContentAdvisory.Explicit ? ContentAdvisory.Explicit : null;

        // Several tracks of this name and none in this song's place: only an answer they all give.
        return named.All(t => t.Advisory == ContentAdvisory.Explicit) ? ContentAdvisory.Explicit : null;
    }

    /// <summary>
    /// One queued file's wanted tags with the flag filled from the release, or the same instance when nothing
    /// changed. A flag that differs between the file and the queue row was set in the queue, by a person or by a
    /// release they picked, and is left alone.
    /// </summary>
    public static TrackInfo Fill(TrackInfo wanted, TrackInfo current, ReleaseDetail release, int songsOnRecord)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(current);
        if (wanted.Advisory != current.Advisory)
            return wanted;

        var song = new Song(wanted.Title, wanted.DurationSeconds, wanted.DiscNumber, wanted.TrackNumber, wanted.Advisory);
        return Decide(song, release, songsOnRecord) is { } flag && flag != wanted.Advisory
            ? wanted with { Advisory = flag, TagsEdited = true }
            : wanted;
    }

    /// <summary>A planned change to a song already in the library: the row, the flag it has now, and the flag it
    /// gets.</summary>
    public sealed record Change(LibraryTrack Row, ContentAdvisory Was, ContentAdvisory Becomes);

    /// <summary>The changes a release gives for the songs of one record in the library. Rows named in
    /// <paramref name="leave"/> (by relative path) are not changed.</summary>
    public static IReadOnlyList<Change> Plan(
        IReadOnlyList<LibraryTrack> rows, ReleaseDetail release, IReadOnlySet<string>? leave = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(release);

        var changes = new List<Change>();
        foreach (var row in rows)
        {
            if (leave?.Contains(row.RelativePath) == true)
                continue;
            var was = row.Advisory ?? ContentAdvisory.Unknown;
            var song = new Song(row.Title, row.DurationSeconds, row.DiscNumber, row.TrackNumber, was);
            if (Decide(song, release, rows.Count) is { } flag && flag != was)
                changes.Add(new Change(row, was, flag));
        }
        return changes;
    }

    /// <summary>
    /// The record a set of library rows is, for the catalogue search, or null when there is nothing to ask:
    /// no album title, no artist, or no song whose flag this fill could change.
    /// </summary>
    /// <param name="releaseId">The MusicBrainz release the album is linked to, if it is.</param>
    public static CatalogueFill.Album? AlbumOf(IReadOnlyList<LibraryTrack> rows, string? releaseId)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0 || !rows.Any(r => Wants(r.Advisory ?? ContentAdvisory.Unknown)))
            return null;

        var title = MostCommon(rows.Select(r => r.Album is { } a ? AlbumResolver.NormalizeAlbum(a) : null));
        var artist = MostCommon(rows.Select(r => r.AlbumArtist)) ?? MostCommon(rows.Select(r => r.Artist));
        if (title is null || artist is null)
            return null;

        return new CatalogueFill.Album(artist, title, rows.Count, string.IsNullOrWhiteSpace(releaseId) ? null : releaseId);

        static string? MostCommon(IEnumerable<string?> values)
            => values.Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!.Trim())
                .GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault();
    }

    /// <summary>
    /// Writes planned changes into the files and the library, the way a tag edit made in the app is written: the
    /// flag goes into the file (that field only), the row is read again from the file, and the row is marked as
    /// edited on this computer, so the cloud sync sends the flag to the catalogue and the sidecar.
    ///
    /// <para>A file is skipped when <paramref name="busy"/> says it is in use, when it is missing, when it is in a
    /// kept library (rpm never writes into those files), or when its flag is no longer the one the plan was made
    /// from. Best effort per file: one that refuses the write is left as it was.</para>
    /// </summary>
    /// <returns>How many files were changed.</returns>
    public static int Write(LibraryDatabase db, string root, IReadOnlyList<Change> changes, Func<string, bool>? busy = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0 || KeptFiles.Covers(root))
            return 0;

        var rows = new List<LibraryTrack>();
        foreach (var change in changes)
        {
            if (change.Row.IsOffloaded)
                continue;
            var path = Path.Combine(root, change.Row.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path) || busy?.Invoke(path) == true)
                continue;
            try
            {
                if (!TagReader.WriteAdvisory(path, change.Was, change.Becomes))
                    continue;
                rows.Add(LibraryDatabase.FromTrackInfo(TagReader.Read(path), root));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TagLib.CorruptFileException
                                           or TagLib.UnsupportedFormatException or InvalidOperationException)
            {
                // Locked, read-only or unreadable: left as it was. The next import's pass may reach it.
            }
        }

        if (rows.Count == 0)
            return 0;
        db.UpsertMany(rows);
        db.NoteMetadataEdited(rows.Select(r => r.RelativePath), DateTime.UtcNow.Ticks);
        return rows.Count;
    }

    /// <summary>A trailing note in brackets: "(Remastered 2011)", "[Explicit]".</summary>
    [GeneratedRegex(@"\s*[\(\[][^\(\)\[\]]*[\)\]]\s*$")]
    private static partial Regex TrailingNote();

    /// <summary>A "feat." credit at the end of a title.</summary>
    [GeneratedRegex(@"\s+(?:feat\.?|ft\.?|featuring)\s+.*$", RegexOptions.IgnoreCase)]
    private static partial Regex Featuring();

    /// <summary>A title as the match compares it.</summary>
    internal static string TitleKey(string? title)
    {
        var (bare, _) = ExplicitTitleMark.Split(title);
        var text = bare ?? "";
        // Notes can be stacked: "Song (feat. X) [Remastered]".
        for (var i = 0; i < 3; i++)
        {
            var shorter = TrailingNote().Replace(text, "");
            if (shorter.Length == 0 || shorter == text)
                break;
            text = shorter;
        }
        return SimilarAlbums.Fold(Featuring().Replace(text, ""));
    }

    private static bool Near(double ours, double? theirs, double within)
        => ours <= 0 || theirs is not > 0 || Math.Abs(ours - theirs.Value) <= within;

    /// <summary>Whether a listing track is in the song's place on the record. A listing with one disc numbers
    /// its tracks by <see cref="ReleaseTrackInfo.Position"/>; one with several by disc and place on the disc.</summary>
    private static bool SamePlace(Song song, ReleaseTrackInfo track, ReleaseDetail release)
    {
        if (song.Track == 0)
            return false;
        var disc = song.Disc == 0 ? 1u : song.Disc;
        if (track.Disc == 0 || release.DiscCount <= 1)
            return disc == 1 && (track.DiscPosition != 0 ? track.DiscPosition : track.Position) == song.Track;
        return track.Disc == disc && track.DiscPosition == song.Track;
    }
}

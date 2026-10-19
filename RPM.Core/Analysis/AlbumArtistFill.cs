using System.Text.RegularExpressions;
using RPM.Core.Models;

namespace RPM.Core.Analysis;

/// <summary>
/// The album artist that a set of files lacks, worked out from the files themselves.
///
/// <para>Many files carry no album-artist tag: rips from tools that never wrote one, files tagged
/// by hand, files copied from a phone. The field matters because the library files a record under
/// it. Without it, a record reaches the import queue split across as many artists as it has guest
/// singers. The value is not a guess: the other tracks of the same record already say it. If all
/// tracks have one artist, that artist is the record's. If there is more than one, the record is a
/// compilation, which is what <see cref="AlbumResolver.VariousArtists"/> means.</para>
///
/// <para>Only ever fills a BLANK. A file that carries an album artist was told what it is by a
/// rip, a tagger or a person, and this code does not override it. On a record where only some
/// files carry one, that answer is copied to the rest and no vote is taken.</para>
/// </summary>
public static partial class AlbumArtistFill
{
    /// <summary>A guest credit, which is part of the TRACK's artist and not of the record's: without
    /// this, one featured singer turns a solo album into a compilation.</summary>
    [GeneratedRegex(@"\s*[\(\[]?\s*(feat\.?|featuring|ft\.?)\s.*$", RegexOptions.IgnoreCase)]
    private static partial Regex GuestCredit();

    /// <summary>
    /// The same tracks, each with an album artist where it had none. Order is preserved, so callers
    /// can pair the result with what they passed in. The queue keeps the file's own reading as
    /// CURRENT and takes this as the WANTED side, because a deduced field is a proposal and the file
    /// on disk still says nothing.
    ///
    /// <para>A track this fills is marked <see cref="TrackInfo.TagsEdited"/>, which is what carries
    /// the answer past the import. Without it, a file whose tags were already good would be moved
    /// without being written to at all: the deduced album artist would reach the FOLDER name and
    /// nothing else, the file would still say nothing, and the mirror, which re-reads the file, would
    /// agree with it.</para>
    /// </summary>
    public static IReadOnlyList<TrackInfo> Fill(IReadOnlyList<TrackInfo> tracks)
    {
        // A record is one album name in one folder. The name alone would merge every "Greatest Hits"
        // in a bulk import into one enormous compilation; the folder alone would split a two-disc set
        // and merge a folder somebody dropped three albums into.
        var byAlbum = tracks
            .Where(t => !string.IsNullOrWhiteSpace(t.Album))
            .GroupBy(t => (Folder: AlbumResolver.DiscFolderParent(t.FilePath),
                           Album: AlbumResolver.NormalizeAlbum(t.Album!)),
                     AlbumKeyComparer.Instance);

        var answers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var album in byAlbum)
        {
            if (AlbumArtistFor(album.ToList()) is not { } artist)
                continue;
            foreach (var track in album)
                answers[track.FilePath] = artist;
        }

        if (answers.Count == 0)
            return tracks;

        var filled = new List<TrackInfo>(tracks.Count);
        foreach (var track in tracks)
        {
            filled.Add(string.IsNullOrWhiteSpace(track.AlbumArtist)
                       && answers.TryGetValue(track.FilePath, out var artist)
                ? track with { AlbumArtist = artist, TagsEdited = true }
                : track);
        }
        return filled;
    }

    /// <summary>
    /// Whether the record is a various-artists one: a compilation, a soundtrack, a "Now" release.
    ///
    /// <para>Asked because it changes what a tracklist has to say. On a record by one artist the
    /// heading has already named who is playing. On this kind of record every track has its own
    /// artist, which only the track's own line can show.</para>
    /// </summary>
    public static bool IsVariousArtists(IReadOnlyList<TrackInfo> album)
    {
        ArgumentNullException.ThrowIfNull(album);
        return AlbumArtistFor(album) is { } artist
               && artist.Equals(AlbumResolver.VariousArtists, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// What one record's files say the record is by, or null when they say nothing. An album artist
    /// already on one of them is the answer for all of them: somebody filled it in, so it is not a
    /// coincidence to be overruled by a vote. Where none of them carries one, the track artists
    /// decide: one artist is that artist, more than one is a compilation.
    /// </summary>
    private static string? AlbumArtistFor(IReadOnlyList<TrackInfo> album)
    {
        var tagged = Distinct(album.Select(t => t.AlbumArtist));
        if (tagged.Count > 0)
            return tagged.Count == 1 ? tagged[0] : AlbumResolver.VariousArtists;

        // Guest credits are the track's, not the record's: "Beyoncé" and "Beyoncé feat. Jay-Z" are one
        // artist, and the name that goes on the shelf is the one without the guest.
        var artists = album.Select(t => t.Artist)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => GuestCredit().Replace(a!.Trim(), "").Trim())
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return artists.Count switch
        {
            0 => null,
            1 => artists[0],
            _ => AlbumResolver.VariousArtists,
        };
    }

    private static List<string> Distinct(IEnumerable<string?> values) => values
        .Where(v => !string.IsNullOrWhiteSpace(v))
        .Select(v => v!.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>Case-insensitive on both halves of the album key.</summary>
    private sealed class AlbumKeyComparer : IEqualityComparer<(string Folder, string Album)>
    {
        public static readonly AlbumKeyComparer Instance = new();

        public bool Equals((string Folder, string Album) x, (string Folder, string Album) y)
            => StringComparer.OrdinalIgnoreCase.Equals(x.Folder, y.Folder)
               && StringComparer.OrdinalIgnoreCase.Equals(x.Album, y.Album);

        public int GetHashCode((string Folder, string Album) key)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(key.Folder),
                StringComparer.OrdinalIgnoreCase.GetHashCode(key.Album));
    }
}

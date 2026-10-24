using RPM.Core.Models;

namespace RPM.Core.Library;

/// <summary>One disc's slice of an album tracklist: the disc's number, the name the release gives it (when
/// it has one), and its tracks in playing order.</summary>
public sealed record AlbumDiscSection(uint DiscNumber, string? Subtitle, IReadOnlyList<TrackInfo> Tracks);

/// <summary>The reading order of an album's tracks.
///
/// A merged multi-disc album keeps track numbers per disc. Disc 2, track 1 is TRACKNUMBER 1 with DISCNUMBER
/// 2 (see <see cref="Analysis.DiscNumbering"/>). The library returns a folder's rows in file-name order.
/// Sorted that way the discs interleave: 1, 1, 2, 2, 3, 3. No one reads a double album like that. These
/// methods put each disc back together, with discs in order and tracks in order inside each one. A view can
/// then draw disc 1 whole, a divider, and then disc 2.
/// </summary>
public static class AlbumTracklist
{
    /// <summary>Where a tracklist drawn in two columns breaks: the index of the first line in the RIGHT
    /// column.
    ///
    /// <para><paramref name="lineUnits"/> is the height of each line in track units: one for a track, two
    /// for a disc divider or a work header. The cut is made by height, not by line count. On a multi-disc
    /// set the dividers make one column longer than the other if lines are counted. A view that reserves
    /// room before it reads the tracks, such as the album list, needs the taller column to be exactly half
    /// the total units, rounded up.</para>
    /// </summary>
    public static int ColumnCut(IReadOnlyList<int> lineUnits)
    {
        var half = (lineUnits.Sum() + 1) / 2;
        var taken = 0;
        for (var i = 0; i < lineUnits.Count; i++)
        {
            if (taken >= half)
                return i;
            taken += lineUnits[i];
        }
        return lineUnits.Count;
    }

    /// <summary>The album's tracks grouped by disc, with discs in ascending order and tracks ascending
    /// inside each disc. Untracked files keep their incoming order at the end of their disc. A track with
    /// no disc number counts as disc 1. So a single-disc album, which is the common case, gives exactly one
    /// section, and a view can skip the disc headers.
    /// </summary>
    public static List<AlbumDiscSection> ByDisc(IReadOnlyList<TrackInfo> tracks)
        => [.. ByDisc(tracks, t => t.DiscNumber, t => t.TrackNumber)
                .Select(s => new AlbumDiscSection(
                    s.DiscNumber,
                    s.Tracks.Select(t => t.DiscSubtitle).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
                    s.Tracks))];

    /// <summary>The same grouping for any row type that has a disc and a track number, such as the cloud's
    /// rows, which the phone uses to draw an album. Both must use one rule, so they agree on where disc 2
    /// starts. A disc has no subtitle here. Only a file's tags carry one.
    /// </summary>
    public static List<(uint DiscNumber, List<T> Tracks)> ByDisc<T>(
        IReadOnlyList<T> tracks, Func<T, uint> discOf, Func<T, uint> trackOf)
    {
        var sections = new List<(uint, List<T>)>();
        foreach (var group in tracks.GroupBy(t => discOf(t) == 0 ? 1u : discOf(t)).OrderBy(g => g.Key))
            // Zero means no number. Those files sort after the numbered ones, not before track 1. OrderBy
            // is stable, so tied items keep the order the caller gave.
            sections.Add((group.Key, [.. group.OrderBy(t => trackOf(t) == 0 ? uint.MaxValue : trackOf(t))]));

        return sections;
    }

    /// <summary>A disc's tracks grouped into runs of one work. A classical listing is a list of works,
    /// each with its movements underneath. That is how it is published, ripped and read.
    ///
    /// <para>A run is CONSECUTIVE tracks only, not every track that names the same work. A recital that
    /// plays two nocturnes with a mazurka between them gives three groups, not two. A symphony whose last
    /// movement is on the next disc of a box gives two groups. That matches the physical discs, and
    /// anything that queues them must follow the same order.</para>
    ///
    /// <para>Tracks with no work form a group whose <c>Work</c> is null. A record with no works at all is
    /// therefore one group with no name, so a caller needs only one shape. Call this for each disc section.
    /// Works do not span discs. Sorting with <see cref="ByDisc(IReadOnlyList{TrackInfo})"/> first gives the
    /// full ordering.</para>
    /// </summary>
    public static List<(string? Work, List<T> Tracks)> ByWork<T>(
        IReadOnlyList<T> tracks, Func<T, string?> workOf)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(workOf);

        var groups = new List<(string? Work, List<T> Tracks)>();
        foreach (var track in tracks)
        {
            // Blank and null mean the same thing: this track is not part of a work. A listing that writes
            // one where it means the other must not open a second group.
            var work = workOf(track) is { } named && !string.IsNullOrWhiteSpace(named) ? named : null;

            if (groups.Count == 0 || !string.Equals(groups[^1].Work, work, StringComparison.Ordinal))
                groups.Add((work, []));

            groups[^1].Tracks.Add(track);
        }

        return groups;
    }

    /// <summary>The same runs for an album's own tracks.</summary>
    public static List<(string? Work, List<TrackInfo> Tracks)> ByWork(IReadOnlyList<TrackInfo> tracks)
        => ByWork(tracks, t => t.Work);

    /// <summary>
    /// What a movement's own line says, under a work heading that already gives the rest of the name.
    ///
    /// <para>A file's title carries the whole name, such as "Sonata In C Major, KV. 330: Allegretto", because
    /// every program that does not know about works reads that field. Under the heading, the sonata's name
    /// would then appear on every line. So the movement is shown alone: the movement tag where the file has
    /// one, and otherwise what is left of the title once the work in front of it is removed.</para>
    ///
    /// <para>A movement tag that repeats the work is cut in the same way. MusicBrainz Picard writes
    /// "Der Rosenkavalier, op. 59: Akt I. Einleitung" as the movement of the work
    /// "Der Rosenkavalier, op. 59: Akt I", so without the cut every movement would say the whole title
    /// again. The marks that part the work from the movement (see <see cref="WorkSeparators"/>) go with
    /// it.</para>
    /// </summary>
    /// <param name="movementName">The file's MOVEMENTNAME tag, or null.</param>
    /// <param name="title">The file's title, or null.</param>
    /// <param name="work">The work the track is a movement of. Null gives null: a track that is not part of
    /// a work has no movement to show.</param>
    /// <returns>The movement alone, or null when neither the tag nor the title says one.</returns>
    public static string? MovementOf(string? movementName, string? title, string? work)
    {
        if (string.IsNullOrWhiteSpace(work))
            return null;

        if (!string.IsNullOrWhiteSpace(movementName))
            return AfterWork(movementName, work) ?? movementName;

        return AfterWork(title, work);
    }

    /// <summary>The same for an album's own track.</summary>
    public static string? MovementOf(TrackInfo track, string? work)
        => MovementOf(track.MovementName, track.Title, work);

    /// <summary>The marks that stand between a work and its movement in a title: "Work: Movement",
    /// "Work. Movement", "Work - Movement" and the like. Taken off the front of what is left.</summary>
    private static readonly char[] WorkSeparators = [' ', '.', ',', ':', ';', '-', '–', '—'];

    /// <summary>What <paramref name="text"/> has left once <paramref name="work"/> in front of it and the
    /// marks after the work are removed. Null when the text does not start with the work, or when nothing
    /// is left.</summary>
    private static string? AfterWork(string? text, string work)
    {
        if (text is null || !text.StartsWith(work, StringComparison.CurrentCultureIgnoreCase))
            return null;

        var rest = text[work.Length..].TrimStart(WorkSeparators);
        return rest.Length > 0 ? rest : null;
    }

    /// <summary>The genres whose records are classical music, by the shelf
    /// <see cref="Metadata.Genres.Match"/> puts a tag on.</summary>
    private static readonly string[] ClassicalShelves = ["Classical", "Opera"];

    /// <summary>
    /// Whether most of a record's tracks are filed under classical music or opera by their genre.
    ///
    /// <para>A record is listed as works with their movements when its owner marked it classical, or when
    /// this is true. The work tags alone never decide: MusicBrainz names a work for many pop songs too, so a
    /// pop record can have runs of a work. A genre does decide, because a record filed under Classical or
    /// Opera is classical music whatever else its tags say. Few records are marked by hand, and many more
    /// carry one of these two genres.</para>
    ///
    /// <para>"Most" is more than half. So one classical bonus track does not change a pop record, and one
    /// untagged track does not stop a symphony being read as one.</para>
    /// </summary>
    /// <param name="genreOf">The genre tag of a track, as the file gives it. It is read through
    /// <see cref="Metadata.Genres.Match"/>, so the shelf decides and not the exact spelling.</param>
    public static bool ClassicalByGenre<T>(IReadOnlyList<T> tracks, Func<T, string?> genreOf)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(genreOf);

        if (tracks.Count == 0)
            return false;

        var filed = 0;
        foreach (var track in tracks)
            if (Metadata.Genres.Match(genreOf(track)) is { } shelf
                && Array.IndexOf(ClassicalShelves, shelf) >= 0)
                filed++;

        return filed * 2 > tracks.Count;
    }

    /// <summary>The same for an album's own tracks.</summary>
    public static bool ClassicalByGenre(IReadOnlyList<TrackInfo> tracks)
        => ClassicalByGenre(tracks, t => t.Genre);

    /// <summary>Every track of the album in reading order: disc 1 in full, then disc 2. Play all, the
    /// queue and Add to playlist all use this order.
    /// </summary>
    public static List<TrackInfo> InDiscOrder(IReadOnlyList<TrackInfo> tracks)
        => InDiscOrder(tracks, t => t.DiscNumber, t => t.TrackNumber);

    /// <summary>The same order for any row type that has a disc and a track number, such as the library's
    /// <see cref="LibraryTrack"/>, which the playlist and queue code passes around.
    /// </summary>
    public static List<T> InDiscOrder<T>(IReadOnlyList<T> tracks, Func<T, uint> discOf, Func<T, uint> trackOf)
        => [.. tracks.OrderBy(t => discOf(t) == 0 ? 1u : discOf(t))
                     .ThenBy(t => trackOf(t) == 0 ? uint.MaxValue : trackOf(t))];

    /// <summary>The tracks from <paramref name="start"/> to the end of the list. Play from here and Start
    /// DJing here use this. Tracks are matched by file path, which every list in the app uses as the
    /// identity. A track that is not in the list plays alone, so a file started from outside the list still
    /// plays.
    /// </summary>
    public static List<TrackInfo> From(IReadOnlyList<TrackInfo> tracks, TrackInfo start)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(start);

        int at = -1;
        for (int i = 0; i < tracks.Count; i++)
        {
            if (string.Equals(tracks[i].FilePath, start.FilePath, StringComparison.Ordinal))
            {
                at = i;
                break;
            }
        }

        return at < 0 ? [start] : [.. tracks.Skip(at)];
    }
}

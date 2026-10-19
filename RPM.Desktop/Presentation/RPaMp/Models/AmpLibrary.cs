using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using RPM.Core.Library;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.RPaMp.Models;

/// <summary>Which question the library pane is asking of the collection.</summary>
public enum AmpScope
{
    /// <summary>Everything in the collection.</summary>
    Audio,

    /// <summary>The songs played most, the most played first. Songs never played are not in it.</summary>
    MostPlayed,

    /// <summary>The songs that joined the collection most recently.</summary>
    RecentlyAdded,

    /// <summary>The songs whose files were written most recently.</summary>
    RecentlyModified,

    /// <summary>The songs played most recently, whenever that was.</summary>
    RecentlyPlayed,

    /// <summary>Music the collection holds that has never been played once.</summary>
    NeverPlayed,

    /// <summary>The songs marked as a favourite. This is the equivalent of Winamp's Top Rated, using
    /// the one mark this collection holds.</summary>
    Favorites,

    /// <summary>One named playlist. Which one is carried beside the scope, not in it.</summary>
    Playlist,
}

/// <summary>One row of the artist or album pane: a name, and how many songs it holds.</summary>
/// <param name="Name">The artist or album, or null for the "All" row at the top.</param>
/// <param name="Label">What the pane shows: the name as it is, or "All (11 artists)" in the user's
/// language.</param>
/// <param name="Count">How many songs are behind it.</param>
public sealed record AmpBrowseRow(string? Name, Phrase Label, int Count)
{
    /// <summary>The words on the row.</summary>
    public string Text => Label.Text;
}

/// <summary>The line at the foot of the library pane, before the words.</summary>
/// <param name="ItemsKey"><c>Amp_Items</c>, or <c>Amp_ItemsOne</c> for one song.</param>
/// <param name="Items">How many songs are listed.</param>
/// <param name="Length">How long they play, as a clock.</param>
/// <param name="Size">What they weigh.</param>
/// <param name="Seconds">How long the narrowing took, in seconds to three places.</param>
public sealed record AmpStatus(string ItemsKey, int Items, string Length, Phrase Size, string Seconds)
{
    /// <summary>What the line says.</summary>
    public string Text => Loc.Format("Amp_StatusLine", Loc.Format(ItemsKey, Items), Length, Size.Text, Seconds);
}

/// <summary>The column the list is ordered by and which way, or null for the scope's own order.</summary>
public readonly record struct AmpSort(string Column, bool Descending);

/// <summary>
/// Everything the library pane is currently asking for, in one value.
/// </summary>
/// <remarks>
/// No member has a default. A filter that can be left out is easy to forget, and then the list shows
/// too much. A null <see cref="Artist"/> means every artist. This is a stated answer, not a missing
/// value. <see cref="PlaylistPaths"/> must be given even for the scopes that ignore it.
/// </remarks>
/// <param name="Scope">Which list of the collection is open.</param>
/// <param name="PlaylistPaths">The songs the open playlist holds, by relative path. Null for every
/// scope other than <see cref="AmpScope.Playlist"/>, and for a playlist that cannot be read.</param>
/// <param name="Search">What was typed in the search box. Empty for no search.</param>
/// <param name="Artist">The artist picked in the artist pane, or null for all of them.</param>
/// <param name="Album">The album picked in the album pane, or null for all of them.</param>
/// <param name="Sort">The column a header click put the list in, or null for the scope's own order.</param>
public sealed record AmpQuery(
    AmpScope Scope,
    IReadOnlySet<string>? PlaylistPaths,
    string Search,
    string? Artist,
    string? Album,
    AmpSort? Sort);

/// <summary>What one run of the query left: the two panes, the list under them, and its totals.</summary>
public sealed record AmpResult(
    IReadOnlyList<AmpBrowseRow> Artists,
    IReadOnlyList<AmpBrowseRow> Albums,
    IReadOnlyList<LibraryRow> Tracks,
    double Seconds,
    long Bytes,
    TimeSpan Took);

/// <summary>One song as the library pane lists it.</summary>
/// <param name="Path">Its path relative to the library root, which is how the pane names it back to
/// the collection.</param>
/// <param name="Folder">The folder it sits in, relative to the root. Which album it is, as far as the
/// covers are concerned: that is the name they are filed under.</param>
public sealed record LibraryRow(
    string Path,
    string Folder,
    string Title,
    string Artist,
    string Album,
    string TrackText,
    string DurationText,
    string YearText,
    string Genre,
    string Kind,
    int Plays);

/// <summary>
/// The library pane's reasoning: what the panes list, what the list under them holds, and what the
/// line at the foot adds up to.
///
/// <para>This class has no side effects. Nothing here reads the database, keeps state or touches a
/// control. It takes the collection and what is picked, and returns what to draw. The pane's state is
/// kept on <c>RPaMpModel</c> and the drawing is in the view. The rules are here, so that tests can
/// check them without an application, a window or a dispatcher.</para>
///
/// <para>Filtering runs in memory over the whole collection, which is read once. The panes narrow again
/// on every click, and a database query on each click would repeat the same work.</para>
/// </summary>
public static class AmpLibrary
{
    /// <summary>Runs the query: the panes, the list, and the totals under it.</summary>
    public static AmpResult Run(IReadOnlyList<LibraryTrack> all, AmpQuery query)
    {
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(query);

        var clock = Stopwatch.StartNew();

        // Scope first, then search. The search narrows the open list, not the whole collection, so a
        // search inside Never Played stays inside Never Played.
        var scope = Search(InScope(all, query), query.Search).ToList();

        var artists = Pane(
            scope.Select(ArtistOf),
            scope.Count,
            total => total == 1 ? "Amp_AllArtistsOne" : "Amp_AllArtists");

        // The album pane is narrowed by the chosen artist. The list is narrowed by the chosen artist and
        // album. This is what makes the panes read left to right.
        var byArtist = scope.Where(t => query.Artist is null || Same(ArtistOf(t), query.Artist)).ToList();

        var albums = Pane(
            byArtist.Select(t => t.Album ?? string.Empty),
            byArtist.Count,
            total => total == 1 ? "Amp_AllAlbumsOne" : "Amp_AllAlbums");

        var listed = byArtist
            .Where(t => query.Album is null || Same(t.Album ?? string.Empty, query.Album))
            .ToList();

        var ordered = Order(listed, query).ToList();

        return new AmpResult(
            artists,
            albums,
            [.. ordered.Select(Row)],
            ordered.Sum(t => t.DurationSeconds),
            ordered.Sum(t => t.SizeBytes),
            clock.Elapsed);
    }

    /// <summary>
    /// The line at the foot of the pane: how many songs are listed, how long they play, what they
    /// weigh, and how long the narrowing took. This is Winamp's own status line.
    /// </summary>
    public static AmpStatus Status(int items, double seconds, long bytes, TimeSpan took) => new(
        items == 1 ? "Amp_ItemsOne" : "Amp_Items",
        items,
        Clock(seconds),
        Weight(bytes),
        took.TotalSeconds.ToString("0.000", CultureInfo.CurrentCulture));

    /// <summary>A length as minutes and seconds, or hours where there are any.</summary>
    public static string Clock(double seconds)
    {
        if (seconds <= 0)
            return "0:00";

        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{span.Minutes}:{span.Seconds:00}");
    }

    /// <summary>A size in the unit that reads best, with two decimal places, as Winamp shows it. The unit
    /// is a string key, because French uses different unit names (Ko, Mo, Go).</summary>
    public static Phrase Weight(long bytes)
    {
        if (bytes <= 0)
            return new Phrase("Unit_MB", 0d.ToString("0.00", CultureInfo.CurrentCulture));

        double value = bytes;
        foreach (var unit in new[] { "Unit_KB", "Unit_MB", "Unit_GB", "Unit_TB" })
        {
            value /= 1024;
            if (value < 1024 || unit == "Unit_TB")
                return new Phrase(unit, value.ToString("0.00", CultureInfo.CurrentCulture));
        }

        return new Phrase("Unit_TB", value.ToString("0.00", CultureInfo.CurrentCulture));
    }

    /// <summary>Whether a song matches the search text. Every word must match some field of the song.
    /// Words are matched one at a time, so "beatles revolver" finds the album.</summary>
    public static bool Matches(LibraryTrack track, string search)
    {
        ArgumentNullException.ThrowIfNull(track);

        if (string.IsNullOrWhiteSpace(search))
            return true;

        var hay = string.Join(
            ' ',
            track.Title,
            track.Artist,
            track.AlbumArtist,
            track.Album,
            track.Genre,
            track.FileName);

        foreach (var word in search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (hay.Contains(word, StringComparison.CurrentCultureIgnoreCase))
                continue;

            return false;
        }

        return true;
    }

    /// <summary>The name the panes file a song under: the album artist if the file sets one, otherwise
    /// the track artist. This keeps all tracks of one album under one name.</summary>
    public static string ArtistOf(LibraryTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return track.AlbumArtist is { Length: > 0 } albumArtist ? albumArtist : track.Artist ?? string.Empty;
    }

    /// <summary>The songs the open list holds, before anything is typed or picked.</summary>
    private static IEnumerable<LibraryTrack> InScope(IReadOnlyList<LibraryTrack> all, AmpQuery query) =>
        query.Scope switch
        {
            AmpScope.MostPlayed => all.Where(t => t.Plays > 0),
            AmpScope.RecentlyPlayed => all.Where(t => t.LastPlayedUtcTicks > 0),
            AmpScope.NeverPlayed => all.Where(t => t.Plays == 0),
            AmpScope.Favorites => all.Where(t => t.IsFavorite),

            // A playlist whose songs could not be read shows no songs. It must not show the whole
            // collection, because that would hide the error.
            AmpScope.Playlist => query.PlaylistPaths is { } wanted
                ? all.Where(t => wanted.Contains(t.RelativePath))
                : [],

            _ => all,
        };

    private static IEnumerable<LibraryTrack> Search(IEnumerable<LibraryTrack> tracks, string search) =>
        string.IsNullOrWhiteSpace(search) ? tracks : tracks.Where(t => Matches(t, search));

    /// <summary>
    /// The sort order of the list. This is the column the user clicked, or else the default order of the
    /// open list: most played first for Most Played, newest first for Recently Added.
    /// </summary>
    private static IEnumerable<LibraryTrack> Order(IEnumerable<LibraryTrack> tracks, AmpQuery query)
    {
        if (query.Sort is { } sort)
        {
            return sort.Column switch
            {
                "artist" => Key(t => t.Artist ?? string.Empty),
                "album" => Key(t => t.Album ?? string.Empty),
                "genre" => Key(t => t.Genre ?? string.Empty),
                "kind" => Key(t => t.Extension),
                "track" => Number(t => t.DiscNumber * 1000d + t.TrackNumber),
                "length" => Number(t => t.DurationSeconds),
                "year" => Number(t => t.Year),
                "plays" => Number(t => t.Plays),
                _ => Key(t => t.Title ?? t.FileName),
            };

            IOrderedEnumerable<LibraryTrack> Key(Func<LibraryTrack, string> read) =>
                sort.Descending
                    ? tracks.OrderByDescending(read, StringComparer.CurrentCultureIgnoreCase)
                    : tracks.OrderBy(read, StringComparer.CurrentCultureIgnoreCase);

            IOrderedEnumerable<LibraryTrack> Number(Func<LibraryTrack, double> read) =>
                sort.Descending ? tracks.OrderByDescending(read) : tracks.OrderBy(read);
        }

        return query.Scope switch
        {
            AmpScope.MostPlayed => tracks.OrderByDescending(t => t.Plays),
            AmpScope.RecentlyAdded => tracks.OrderByDescending(t => t.AddedUtcTicks),
            AmpScope.RecentlyModified => tracks.OrderByDescending(t => t.ModifiedUtcTicks),
            AmpScope.RecentlyPlayed => tracks.OrderByDescending(t => t.LastPlayedUtcTicks),
            AmpScope.Favorites => tracks.OrderByDescending(t => t.FavoritedUtcTicks),
            _ => tracks
                .OrderBy(t => t.Album, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(t => t.DiscNumber)
                .ThenBy(t => t.TrackNumber),
        };
    }

    /// <summary>One pane: the "All" row, then every distinct name with its count behind it.</summary>
    /// <param name="allKey">The string key of the "All" row for a count of names.</param>
    private static List<AmpBrowseRow> Pane(IEnumerable<string> names, int total, Func<int, string> allKey)
    {
        var counts = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var name in names)
        {
            if (name.Length == 0)
                continue;

            counts.TryGetValue(name, out var had);
            counts[name] = had + 1;
        }

        var rows = new List<AmpBrowseRow>(counts.Count + 1)
        {
            new(null, new Phrase(allKey(counts.Count), counts.Count.ToString(CultureInfo.CurrentCulture)), total),
        };

        rows.AddRange(counts
            .OrderBy(pair => pair.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(pair => new AmpBrowseRow(pair.Key, Phrase.AsIs(pair.Key), pair.Value)));

        return rows;
    }

    private static LibraryRow Row(LibraryTrack track) => new(
        track.RelativePath,
        track.Folder,
        track.Title is { Length: > 0 } title ? title : track.FileName,
        track.Artist is { Length: > 0 } artist ? artist : ArtistOf(track),
        track.Album ?? string.Empty,
        track.TrackNumber > 0 ? track.TrackNumber.ToString(CultureInfo.CurrentCulture) : string.Empty,
        track.DurationSeconds > 0 ? Clock(track.DurationSeconds) : "--:--",
        track.Year > 0 ? track.Year.ToString(CultureInfo.CurrentCulture) : string.Empty,
        track.Genre ?? string.Empty,
        track.Extension.TrimStart('.').ToUpperInvariant(),
        track.Plays);

    private static bool Same(string a, string b) =>
        string.Equals(a, b, StringComparison.CurrentCultureIgnoreCase);
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using RPM.Core.Library;
using RPM.Desktop.Presentation.RPaMp.Models;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Tests;

/// <summary>
/// The RPaMp library pane's reasoning: what each list of the collection holds, what the two name
/// panes say, what the search narrows, and what the line at the foot adds up to.
/// </summary>
public class AmpLibraryTests
{
    private static LibraryTrack Track(
        string path,
        string artist = "Big L",
        string album = "Lifestylez Ov Da Poor & Dangerous",
        string title = "Street Struck",
        int plays = 0,
        bool favorite = false,
        long added = 100,
        double seconds = 249,
        long bytes = 5_000_000,
        uint track = 7) => new()
        {
            RelativePath = path,
            Folder = "Big L",
            FileName = path,
            Extension = ".mp3",
            Title = title,
            Artist = artist,
            AlbumArtist = artist,
            Album = album,
            Genre = "Hip Hop",
            Year = 1995,
            TrackNumber = track,
            Plays = plays,
            LastPlayedUtcTicks = plays > 0 ? 900 : 0,
            IsFavorite = favorite,
            AddedUtcTicks = added,
            DurationSeconds = seconds,
            SizeBytes = bytes,
        };

    private static AmpQuery Query(
        AmpScope scope = AmpScope.Audio,
        string search = "",
        string? artist = null,
        string? album = null,
        AmpSort? sort = null,
        IReadOnlySet<string>? playlist = null) =>
        new(scope, playlist, search, artist, album, sort);

    [Fact]
    public void Audio_lists_the_whole_collection()
    {
        var all = new[] { Track("a.mp3"), Track("b.mp3", artist: "Eminem", album: "Recovery") };

        var result = AmpLibrary.Run(all, Query());

        Assert.Equal(2, result.Tracks.Count);
    }

    [Fact]
    public void The_artist_pane_leads_with_All_and_counts_what_is_behind_each_name()
    {
        var all = new[]
        {
            Track("a.mp3"),
            Track("b.mp3"),
            Track("c.mp3", artist: "Eminem", album: "Recovery"),
        };

        var result = AmpLibrary.Run(all, Query());

        Assert.Equal(new Phrase("Amp_AllArtists", "2"), result.Artists[0].Label);
        Assert.Null(result.Artists[0].Name);
        Assert.Equal(3, result.Artists[0].Count);

        Assert.Equal("Big L", result.Artists[1].Name);
        Assert.Equal(2, result.Artists[1].Count);
        Assert.Equal("Eminem", result.Artists[2].Name);
        Assert.Equal(1, result.Artists[2].Count);
    }

    [Fact]
    public void One_name_in_a_pane_is_named_in_the_singular()
    {
        var result = AmpLibrary.Run([Track("a.mp3")], Query());

        Assert.Equal(new Phrase("Amp_AllArtistsOne", "1"), result.Artists[0].Label);
        Assert.Equal(new Phrase("Amp_AllAlbumsOne", "1"), result.Albums[0].Label);
    }

    [Fact]
    public void The_artist_narrows_the_album_pane_beside_it()
    {
        var all = new[]
        {
            Track("a.mp3"),
            Track("b.mp3", artist: "Eminem", album: "Recovery"),
            Track("c.mp3", artist: "Eminem", album: "The Marshall Mathers LP"),
        };

        var result = AmpLibrary.Run(all, Query(artist: "Eminem"));

        Assert.Equal(
            ["Recovery", "The Marshall Mathers LP"],
            result.Albums.Skip(1).Select(a => a.Name));

        // ...and the list under both panes is the artist's, not the collection's.
        Assert.Equal(2, result.Tracks.Count);
    }

    [Fact]
    public void The_album_narrows_the_list()
    {
        var all = new[]
        {
            Track("a.mp3", artist: "Eminem", album: "Recovery"),
            Track("b.mp3", artist: "Eminem", album: "The Marshall Mathers LP"),
        };

        var result = AmpLibrary.Run(all, Query(artist: "Eminem", album: "Recovery"));

        Assert.Equal("a.mp3", Assert.Single(result.Tracks).Path);
    }

    [Fact]
    public void The_search_takes_every_word_and_looks_at_every_field()
    {
        var all = new[]
        {
            Track("a.mp3", artist: "Big L", album: "Lifestylez", title: "Street Struck"),
            Track("b.mp3", artist: "Eminem", album: "Recovery", title: "Not Afraid"),
        };

        Assert.Equal("a.mp3", Assert.Single(AmpLibrary.Run(all, Query(search: "big struck")).Tracks).Path);
        Assert.Equal("b.mp3", Assert.Single(AmpLibrary.Run(all, Query(search: "recovery")).Tracks).Path);
        Assert.Empty(AmpLibrary.Run(all, Query(search: "big afraid")).Tracks);
    }

    [Fact]
    public void The_search_narrows_the_panes_too()
    {
        var all = new[]
        {
            Track("a.mp3"),
            Track("b.mp3", artist: "Eminem", album: "Recovery"),
        };

        var result = AmpLibrary.Run(all, Query(search: "eminem"));

        Assert.Equal(new Phrase("Amp_AllArtistsOne", "1"), result.Artists[0].Label);
        Assert.Equal("Eminem", result.Artists[1].Name);
    }

    [Fact]
    public void Never_played_holds_what_has_not_been_played_and_most_played_holds_what_has()
    {
        var all = new[]
        {
            Track("played.mp3", plays: 4),
            Track("more.mp3", plays: 9),
            Track("silent.mp3"),
        };

        Assert.Equal("silent.mp3", Assert.Single(AmpLibrary.Run(all, Query(AmpScope.NeverPlayed)).Tracks).Path);

        var most = AmpLibrary.Run(all, Query(AmpScope.MostPlayed)).Tracks;
        Assert.Equal(["more.mp3", "played.mp3"], most.Select(t => t.Path));
    }

    [Fact]
    public void Recently_added_is_newest_first()
    {
        var all = new[]
        {
            Track("old.mp3", added: 10),
            Track("new.mp3", added: 90),
        };

        Assert.Equal(
            ["new.mp3", "old.mp3"],
            AmpLibrary.Run(all, Query(AmpScope.RecentlyAdded)).Tracks.Select(t => t.Path));
    }

    [Fact]
    public void Favourites_holds_only_what_was_marked()
    {
        var all = new[] { Track("loved.mp3", favorite: true), Track("plain.mp3") };

        Assert.Equal("loved.mp3", Assert.Single(AmpLibrary.Run(all, Query(AmpScope.Favorites)).Tracks).Path);
    }

    [Fact]
    public void A_playlist_lists_what_it_holds_and_a_playlist_that_could_not_be_read_lists_nothing()
    {
        var all = new[] { Track("in.mp3"), Track("out.mp3") };
        var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "in.mp3" };

        Assert.Equal(
            "in.mp3",
            Assert.Single(AmpLibrary.Run(all, Query(AmpScope.Playlist, playlist: held)).Tracks).Path);

        // Null paths are a playlist that would not open. It lists nothing rather than widening to the
        // whole collection, which is the failure that would go unnoticed.
        Assert.Empty(AmpLibrary.Run(all, Query(AmpScope.Playlist)).Tracks);
    }

    [Fact]
    public void A_header_click_orders_the_list_and_its_direction_is_the_sort_s()
    {
        var all = new[]
        {
            Track("a.mp3", title: "Zebra"),
            Track("b.mp3", title: "Apple"),
        };

        Assert.Equal(
            ["Apple", "Zebra"],
            AmpLibrary.Run(all, Query(sort: new AmpSort("title", false))).Tracks.Select(t => t.Title));

        Assert.Equal(
            ["Zebra", "Apple"],
            AmpLibrary.Run(all, Query(sort: new AmpSort("title", true))).Tracks.Select(t => t.Title));
    }

    [Fact]
    public void A_stated_sort_beats_the_list_s_own_order()
    {
        var all = new[]
        {
            Track("a.mp3", title: "Apple", plays: 1),
            Track("b.mp3", title: "Zebra", plays: 50),
        };

        // Most Played would put Zebra first. The header says otherwise.
        Assert.Equal(
            ["Apple", "Zebra"],
            AmpLibrary.Run(all, Query(AmpScope.MostPlayed, sort: new AmpSort("title", false)))
                .Tracks.Select(t => t.Title));
    }

    [Fact]
    public void The_list_adds_up_what_it_holds()
    {
        var all = new[]
        {
            Track("a.mp3", seconds: 249, bytes: 4_000_000),
            Track("b.mp3", seconds: 120, bytes: 2_000_000),
        };

        var result = AmpLibrary.Run(all, Query());

        Assert.Equal(369, result.Seconds);
        Assert.Equal(6_000_000, result.Bytes);
    }

    [Fact]
    public void The_status_line_is_the_count_the_length_and_the_weight()
    {
        // Under a stated culture: the numbers in this line are written the reader's way, so on a
        // machine set to Dutch the same call says "415,06 MB in 0,001 sec." and is right to. The
        // words around them are the string table's, and are checked against it below.
        var was = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

        try
        {
            var status = AmpLibrary.Status(13, 3902, 435_226_112, TimeSpan.FromMilliseconds(1));

            Assert.Equal(new AmpStatus("Amp_Items", 13, "1:05:02", new Phrase("Unit_MB", "415.06"), "0.001"), status);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = was;
        }
    }

    [Fact]
    public void One_song_is_an_item_not_items()
    {
        Assert.Equal("Amp_ItemsOne", AmpLibrary.Status(1, 60, 1024 * 1024, TimeSpan.Zero).ItemsKey);
    }

    [Fact]
    public void A_length_under_an_hour_has_no_hours_in_it()
    {
        Assert.Equal("4:09", AmpLibrary.Clock(249));
        Assert.Equal("1:05:02", AmpLibrary.Clock(3902));
        Assert.Equal("0:00", AmpLibrary.Clock(0));
    }

    [Fact]
    public void The_pane_files_a_song_under_its_album_artist_where_the_file_names_one()
    {
        var track = Track("a.mp3") with { Artist = "Big L", AlbumArtist = "Various Artists" };

        Assert.Equal("Various Artists", AmpLibrary.ArtistOf(track));
        Assert.Equal("Big L", AmpLibrary.ArtistOf(track with { AlbumArtist = null }));
    }

    [Fact]
    public void The_tree_opens_on_Audio_and_hangs_the_playlists_under_Playlists()
    {
        var tree = AmpNav.Tree([("Party", 4L), ("Sunday", 9L)]);

        Assert.Equal(new Phrase("Amp_NavAudio", ""), AmpNav.First(tree)?.Label);

        var rows = AmpNav.Rows(tree).Cast<AmpNode>().ToList();
        Assert.Contains(rows, n => n.Label == Phrase.AsIs("Party") && n.PlaylistId == 4 && n.Scope == AmpScope.Playlist);
        Assert.All(rows.Where(n => n.Depth == 0), n => Assert.True(n.IsGroup));
    }

    [Fact]
    public void A_shut_heading_hides_its_lists()
    {
        var tree = AmpNav.Tree([("Party", 4L)]);
        tree[0].IsExpanded = false;

        var rows = AmpNav.Rows(tree).Cast<AmpNode>().Select(n => n.Label).ToList();

        Assert.DoesNotContain(new Phrase("Amp_NavAudio", ""), rows);
        Assert.Contains(new Phrase("Amp_NavLocal", ""), rows);
        Assert.Contains(Phrase.AsIs("Party"), rows);
    }

    /// <summary>The words of the tree, the panes and the status line are handed out as keys rather than
    /// written at a <c>Loc.Get</c> call, so the repo-wide guard over <c>strings.json</c> cannot see them.
    /// Each is checked against the file: in all five languages, and a count showing the number it is
    /// handed.</summary>
    [Fact]
    public void Every_key_the_pane_hands_out_is_in_the_string_table()
    {
        var one = AmpLibrary.Run([Track("a.mp3")], Query());
        var two = AmpLibrary.Run([Track("a.mp3"), Track("b.mp3", artist: "Eminem", album: "Recovery")], Query());

        var keys = new[]
        {
            one.Artists[0].Label.StringKey, one.Albums[0].Label.StringKey, two.Artists[0].Label.StringKey, two.Albums[0].Label.StringKey,
            AmpLibrary.Status(1, 0, 0, TimeSpan.Zero).ItemsKey, AmpLibrary.Status(2, 0, 0, TimeSpan.Zero).ItemsKey,
            AmpLibrary.Weight(0).StringKey, AmpLibrary.Weight(2048).StringKey, AmpLibrary.Weight(5L << 20).StringKey,
            AmpLibrary.Weight(5L << 30).StringKey, AmpLibrary.Weight(5L << 40).StringKey,
        };

        // The tree's lists and headings take no number.
        var tree = AmpNav.Tree([("Party", 4L)]);
        var named = tree.Concat(tree.SelectMany(n => n.Children)).Select(n => n.Label.StringKey).OfType<string>().ToList();
        Assert.Equal(9, named.Count);

        using var doc = JsonDocument.Parse(File.ReadAllText(SharedFiles.Path("strings.json")));
        var problems = new List<string>();
        foreach (var key in keys.Distinct())
        {
            Assert.NotNull(key);
            if (!doc.RootElement.TryGetProperty(key!, out var entry))
            {
                problems.Add($"{key} is not in strings.json");
                continue;
            }

            foreach (var locale in new[] { "en", "fr", "es", "de", "zh-Hans" })
            {
                var text = entry.TryGetProperty(locale, out var t) ? t.GetString() : null;
                if (text is null || !text.Contains("{0}"))
                    problems.Add($"{key}: {locale} does not show the number");
            }
        }

        foreach (var key in named)
        {
            if (!doc.RootElement.TryGetProperty(key, out var entry))
            {
                problems.Add($"{key} is not in strings.json");
                continue;
            }

            foreach (var locale in new[] { "en", "fr", "es", "de", "zh-Hans" })
                if (!entry.TryGetProperty(locale, out var t) || string.IsNullOrWhiteSpace(t.GetString()))
                    problems.Add($"{key}: no {locale}");
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>Walks up from the test binaries to the folder holding RPM.sln.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RPM.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}

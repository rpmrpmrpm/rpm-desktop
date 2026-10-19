using RPM.Core.Analysis;
using RPM.Core.Library;
using RPM.Core.Metadata;

namespace RPM.Core.Tests;

/// <summary>
/// What a like keeps a song through, and what a dislike does not reach.
///
/// <para><see cref="TrackVerdict.Liked"/> promises that a liked song survives "a cap or a narrowing".
/// It has to be honoured in every narrowing and in both caps, so these pin all four: the decade
/// window, the style narrowing, the per-album cap and the per-artist cap.</para>
/// </summary>
public class LikeSurvivesTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public LikeSurvivesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmlike-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static MeasuredMood Row(
        string path, string folder, double at, string artist = "act", int year = 1990) =>
        new(path, folder, at, at, at, IsExplicit: false, Artist: artist, Year: year,
            LyricValence: null, Loudness: null);

    /// <summary>Every folder filed under Rock. Deduped, because the fixtures deliberately put several
    /// songs on one album and a folder is named once per song.</summary>
    private static Dictionary<string, string> Rock(params string[] folders) =>
        folders.Distinct(StringComparer.OrdinalIgnoreCase)
               .ToDictionary(f => f, _ => "Rock", StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, CatalogueFacts> NoStyles() =>
        new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, IReadOnlySet<SmartPlaylistKind>> Likes(
        params (string Path, SmartPlaylistKind Kind)[] said)
    {
        var map = new Dictionary<string, IReadOnlySet<SmartPlaylistKind>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, kind) in said)
        {
            if (!map.TryGetValue(path, out var kinds))
                map[path] = kinds = new HashSet<SmartPlaylistKind>();
            ((HashSet<SmartPlaylistKind>)kinds).Add(kind);
        }

        return map;
    }

    // ── the caps ──

    /// <summary>
    /// A liked song is not skipped for being the second song from its album, and an unliked one still
    /// is.
    ///
    /// <para>Three songs from one album, the middle one liked. The cap is one song per album, so
    /// without the exemption only the first would appear.</para>
    /// </summary>
    [Fact]
    public void A_like_survives_the_per_record_cap()
    {
        var rows = new List<MeasuredMood>();
        for (var i = 0; i < 60; i++)
            rows.Add(Row($"other/{i:D2}.flac", $"other-{i:D2}", (i + 1) / 61.0, $"act {i:D2}"));

        // Three from one album, all calm enough to be in the band.
        for (var i = 0; i < 3; i++)
            rows.Add(Row($"one/{i}.flac", "one", 0.002 + (i * 0.0001), "one act"));

        var shelf = MoodShelf.Build(
            rows, Rock([.. rows.Select(r => r.Folder)]), NoStyles(),
            Likes(("one/1.flac", SmartPlaylistKind.Calm)));

        var list = shelf.Calm.WholeList.Window(50, 0);

        Assert.Contains("one/0.flac", list);
        Assert.Contains("one/1.flac", list);
        Assert.DoesNotContain("one/2.flac", list);
    }

    /// <summary>A like is exempt from the caps but still counts towards them, which is what stops the
    /// exemption becoming a gap that one album can use to fill the list.</summary>
    [Fact]
    public void A_liked_song_still_counts_towards_the_cap()
    {
        var rows = new List<MeasuredMood>();
        for (var i = 0; i < 60; i++)
            rows.Add(Row($"other/{i:D2}.flac", $"other-{i:D2}", (i + 1) / 61.0, $"act {i:D2}"));

        // Six by one artist across six albums, the first five liked. The artist cap is five.
        for (var i = 0; i < 6; i++)
            rows.Add(Row($"band/{i}.flac", $"band-{i}", 0.002 + (i * 0.0001), "one band"));

        var shelf = MoodShelf.Build(
            rows, Rock([.. rows.Select(r => r.Folder)]), NoStyles(),
            Likes([.. Enumerable.Range(0, 5).Select(i => ($"band/{i}.flac", SmartPlaylistKind.Calm))]));

        var list = shelf.Calm.WholeList.Window(50, 0);

        Assert.Equal(5, list.Count(p => p.StartsWith("band/", StringComparison.Ordinal)));
        Assert.DoesNotContain("band/5.flac", list);
    }

    // ── the narrowings ──

    /// <summary>A liked song is the kind of thing the list is named after, whatever the
    /// catalogue data said, because the listener said so and the styles are only evidence.</summary>
    [Fact]
    public void A_like_survives_the_style_narrowing()
    {
        var rows = new List<MeasuredMood>();
        var facts = new Dictionary<string, CatalogueFacts>(StringComparer.OrdinalIgnoreCase);

        // Enough songs on a preferred style that the narrowing actually fires.
        for (var i = 0; i < 60; i++)
        {
            var folder = $"soft-{i:D2}";
            rows.Add(Row($"{folder}/1.flac", folder, (i + 1) / 200.0, $"act {i:D2}"));
            facts[folder] = new CatalogueFacts("Rock", ["Soft Rock"]);
        }

        // One calm song the catalogues filed under nothing the list prefers.
        rows.Add(Row("odd/1.flac", "odd", 0.001, "odd act"));
        facts["odd"] = new CatalogueFacts("Rock", ["Hard Rock"]);

        var shelf = MoodShelf.Build(
            rows, Rock([.. rows.Select(r => r.Folder)]), facts,
            Likes(("odd/1.flac", SmartPlaylistKind.Calm)));

        var narrowed = shelf.Calm.Within(["Rock"], MoodFlavour.Styles(SmartPlaylistKind.Calm, "Rock"));

        Assert.Contains("odd/1.flac", narrowed.Songs);
    }

    /// <summary>A like also survives the decade window, as it does the other narrowings.</summary>
    [Fact]
    public void A_like_survives_the_decade_window()
    {
        var rows = new List<MeasuredMood>();
        for (var i = 0; i < 200; i++)
            rows.Add(Row($"nineties/{i:D3}.flac", $"nineties-{i:D3}", (i + 1) / 201.0, $"act {i:D3}", 1995));

        // One calm song from far outside the decade the shelf is mostly in.
        rows.Add(Row("old/1.flac", "old", 0.001, "old act", 1962));

        var shelf = MoodShelf.Build(
            rows, Rock([.. rows.Select(r => r.Folder)]), NoStyles(),
            Likes(("old/1.flac", SmartPlaylistKind.Calm)));

        Assert.Contains("old/1.flac", shelf.Calm.WholeList.Songs);
    }

    // ── the scope of both verdicts ──

    /// <summary>A like is said about one list. Liking a song in Chill says nothing about Dancefloor,
    /// so it must not be kept through Dancefloor's cap by a Chill like.</summary>
    [Fact]
    public void A_like_said_of_one_list_is_not_read_by_another()
    {
        var rows = new List<MeasuredMood>();
        for (var i = 0; i < 60; i++)
            rows.Add(Row($"other/{i:D2}.flac", $"other-{i:D2}", (i + 1) / 61.0, $"act {i:D2}"));
        for (var i = 0; i < 3; i++)
            rows.Add(Row($"one/{i}.flac", "one", 0.002 + (i * 0.0001), "one act"));

        // Liked on the DANCEFLOOR, while the list being read is Chill.
        var shelf = MoodShelf.Build(
            rows, Rock([.. rows.Select(r => r.Folder)]), NoStyles(),
            Likes(("one/1.flac", SmartPlaylistKind.Danceable)));

        var list = shelf.Calm.WholeList.Window(50, 0);

        Assert.DoesNotContain("one/1.flac", list);
    }

    /// <summary>
    /// A dislike is said about one list too: the song goes from that one and stays in the others.
    ///
    /// <para>This is the reason <c>track_verdict</c> is keyed by the kind as well as the
    /// path. Asked of the database, because the dislike is applied in SQL rather than on the pool.
    /// </para>
    /// </summary>
    [Fact]
    public void A_dislike_is_said_of_one_list_and_leaves_the_others_alone()
    {
        _db.Upsert(new LibraryTrack
        {
            RelativePath = "a/1.flac",
            Folder = "a",
            FileName = "1.flac",
            Extension = ".flac",
            SizeBytes = 100,
            ModifiedUtcTicks = 1,
            DurationSeconds = 240,
        });
        _db.SetTrackFavorite("a/1.flac", true, 1);

        _db.SetTrackVerdict("a/1.flac", SmartPlaylistKind.Calm, TrackVerdict.Disliked, 1);

        var favourites = _db.QuerySmartPlaylist(
            SmartPlaylistId.WholeShelf(SmartPlaylistKind.FavoriteTracks), nowUtcTicks: 0, advance: true);

        Assert.Contains(favourites, t => t.RelativePath == "a/1.flac");

        _db.SetTrackVerdict("a/1.flac", SmartPlaylistKind.FavoriteTracks, TrackVerdict.Disliked, 1);

        var after = _db.QuerySmartPlaylist(
            SmartPlaylistId.WholeShelf(SmartPlaylistKind.FavoriteTracks), nowUtcTicks: 0, advance: true);

        Assert.DoesNotContain(after, t => t.RelativePath == "a/1.flac");
    }
}

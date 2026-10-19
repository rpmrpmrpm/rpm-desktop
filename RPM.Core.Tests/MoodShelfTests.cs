using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// The three lists a session takes its mood playlists from: which songs are in them, and in what order.
///
/// <para>The order matters. A generated list stops at <see cref="SmartPlaylists.MaxTracks"/>, so the
/// ranking decides what is IN the list, not only how it looks.</para>
/// </summary>
public class MoodShelfTests
{
    /// <summary>The year is zero throughout, on purpose: nothing in this file is about eras, and
    /// <c>MoodList.InOneEra</c> leaves an undated shelf exactly as it is, so the era step does not
    /// affect any assertion below. The eras have their own file.</summary>
    private static MeasuredMood Track(
        string folder, double brightness, double onsets, double dynamics, int n,
        bool explicitLyrics = false, string artist = "", int year = 0) =>
        new($"{folder}/{n}.flac", folder, brightness, onsets, dynamics, explicitLyrics, artist, year,
            // No loudness: this file is about the ranges and the caps. A loudness would be a second
            // thing changing, and MoodBandsTests tests the loud condition.
            LyricValence: null, Loudness: null);

    /// <summary>A collection with no catalogue facts. These tests are about the ranges and the caps;
    /// a flavour narrowing on top would be a second thing changing. The narrowing has its own tests in
    /// <c>MoodFlavourTests</c>.</summary>
    private static readonly Dictionary<string, CatalogueFacts> NoCatalogue = new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> Filed(params (string Folder, string Genre)[] pairs)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, genre) in pairs)
            map[folder] = genre;
        return map;
    }

    /// <summary>
    /// A shelf whose numbers rise evenly, so every percentile is known in advance.
    ///
    /// <para>One record every two tracks, because that is what a real shelf looks like. One folder of
    /// a hundred songs is not a real album, and the record cap would correctly cut it to two.</para>
    /// </summary>
    private static List<MeasuredMood> Ramp(string folder, int count)
    {
        var rows = new List<MeasuredMood>(count);
        for (int i = 0; i < count; i++)
        {
            double at = (double)i / (count - 1);
            rows.Add(Track($"{folder}-{i / 2}", at, at, at, i, artist: $"{folder} act {i / 2}"));
        }
        return rows;
    }

    /// <summary>Every folder these rows are in, filed under one genre. Used for the ramps, which make
    /// their own records.</summary>
    private static Dictionary<string, string> FiledAs(IEnumerable<MeasuredMood> rows, string genre)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
            map[r.Folder] = genre;
        return map;
    }

    [Fact]
    public void The_ends_land_in_their_lists_and_the_middle_lands_in_neither()
    {
        var rows = Ramp("rock", 100);
        var filed = FiledAs(rows, "Rock");

        var shelf = MoodShelf.Build(rows, filed, NoCatalogue, Likes.None);

        // By RECORD, not by track: the cap keeps one song from any album, and which of a record's two
        // songs the ranking keeps is not what this test is about.
        Assert.Contains(shelf.Calm.WholeList.Songs,
            p => p.StartsWith(rows[0].Folder + "/", StringComparison.Ordinal));
        Assert.Contains(shelf.Energetic.WholeList.Songs,
            p => p.StartsWith(rows[^1].Folder + "/", StringComparison.Ordinal));
        Assert.DoesNotContain(rows[50].RelativePath, shelf.Calm.WholeList.Songs);
        Assert.DoesNotContain(rows[50].RelativePath, shelf.Energetic.WholeList.Songs);
        Assert.Equal(100, shelf.MeasuredTracks);
    }

    /// <summary>
    /// The main purpose of the within-genre cut, seen from the playlist side: a collection that is
    /// mostly loud dance music with a little quiet classical gives BOTH to the calm list. Cut as one
    /// shelf it would be all classical, and the listener would only see their own folder names.
    /// </summary>
    [Fact]
    public void The_calm_list_draws_from_every_genre_not_just_the_quiet_one()
    {
        var rows = new List<MeasuredMood>();
        for (int i = 0; i < 60; i++)
        {
            double at = (double)i / 59;
            rows.Add(Track("dance", 0.5 + at * 0.4, 6 + at * 6, 2 + at * 2, i));
        }
        for (int i = 0; i < 60; i++)
        {
            double at = (double)i / 59;
            rows.Add(Track("classical", 0.05 + at * 0.2, 0.5 + at * 1.5, 14 + at * 6, i));
        }

        var filed = Filed(("dance", "Electronic / Dance"), ("classical", "Classical"));
        var shelf = MoodShelf.Build(rows, filed, NoCatalogue, Likes.None);

        Assert.Contains(shelf.Calm.WholeList.Songs, p => p.StartsWith("dance/", StringComparison.Ordinal));
        Assert.Contains(shelf.Calm.WholeList.Songs, p => p.StartsWith("classical/", StringComparison.Ordinal));
        Assert.Contains(shelf.Energetic.WholeList.Songs, p => p.StartsWith("dance/", StringComparison.Ordinal));
        Assert.Contains(shelf.Energetic.WholeList.Songs, p => p.StartsWith("classical/", StringComparison.Ordinal));
    }

    /// <summary>Most clearly calm first. The list is read from the top and cut off, so this decides
    /// which songs are in it, not only their appearance.</summary>
    [Fact]
    public void The_calm_list_leads_with_the_calmest()
    {
        var rows = Ramp("rock", 200);
        var filed = FiledAs(rows, "Rock");

        var shelf = MoodShelf.Build(rows, filed, NoCatalogue, Likes.None);

        Assert.Equal(rows[0].RelativePath, shelf.Calm.WholeList.Songs[0]);
        Assert.Equal(rows[^1].RelativePath, shelf.Energetic.WholeList.Songs[0]);
    }

    /// <summary>Two reads of an unchanged shelf return the same order. Confidence values are often
    /// equal, so the tie-break is necessary for a stable list.</summary>
    [Fact]
    public void Two_reads_of_one_shelf_agree()
    {
        var rows = Ramp("rock", 80);
        var filed = FiledAs(rows, "Rock");

        Assert.Equal(
            MoodShelf.Build(rows, filed, NoCatalogue, Likes.None).Calm.WholeList.Songs,
            MoodShelf.Build(rows, filed, NoCatalogue, Likes.None).Calm.WholeList.Songs);
    }

    /// <summary>Danceable is ordered by how far past both of its conditions a track is, because the
    /// band confidence cannot order it: most danceable tracks are in no band, so ranking by confidence
    /// would sort the whole list by zero.</summary>
    [Fact]
    public void The_danceable_list_leads_with_the_most_driving()
    {
        var rows = new List<MeasuredMood>();
        for (int i = 0; i < 60; i++)
        {
            double at = (double)i / 59;
            // Busier and flatter together, so the last row is the most driving track here.
            rows.Add(Track("dance", 0.5, at * 10, 20 - at * 20, i));
        }

        var filed = Filed(("dance", "Electronic / Dance"));
        var shelf = MoodShelf.Build(rows, filed, NoCatalogue, Likes.None);

        Assert.NotEmpty(shelf.Danceable.WholeList.Songs);
        Assert.Equal(rows[^1].RelativePath, shelf.Danceable.WholeList.Songs[0]);
    }

    /// <summary>An unmeasured collection gives three empty lists, not a null to test for, and says so:
    /// nothing measured is different from a collection with no calm music in it.
    /// </summary>
    [Fact]
    public void A_collection_nobody_has_measured_is_empty_and_says_so()
    {
        var shelf = MoodShelf.Build([], Filed(), NoCatalogue, Likes.None);

        Assert.Empty(shelf.Calm.WholeList.Songs);
        Assert.Empty(shelf.Energetic.WholeList.Songs);
        Assert.Empty(shelf.Danceable.WholeList.Songs);
        Assert.Equal(0, shelf.MeasuredTracks);
    }

    // ── one genre at a time ──

    /// <summary>
    /// The list narrowed to a genre holds exactly that genre's songs from the whole list, and ranks
    /// them in its own way.
    ///
    /// <para><b>"Calm rock" is deliberately not a PREFIX of "calm".</b> The two lists answer
    /// different questions: the pooled one wants the calmest songs of any kind, and a genre row wants
    /// that and also wants the answer to be rock. So the row prefers songs that sound like the middle
    /// of their own genre, by a limited amount (see <c>MoodBands.TypicalFor</c> and
    /// <c>MoodList.Within</c>).</para>
    ///
    /// <para>What this test checks: the row holds only that genre, and it holds every song of that
    /// genre that the whole list holds. The re-ranking may reorder a row but may never change which
    /// songs are in it.</para>
    /// </summary>
    [Fact]
    public void A_genre_list_holds_exactly_that_genres_songs_out_of_the_whole_list()
    {
        var rows = new List<MeasuredMood>();
        for (int i = 0; i < 60; i++)
        {
            double at = (double)i / 59;
            rows.Add(Track("rock", at, at * 10, at * 20, i));
            rows.Add(Track("jazz", at * 0.5, at * 4, at * 25, i));
        }

        var filed = Filed(("rock", "Rock"), ("jazz", "Jazz"));
        var shelf = MoodShelf.Build(rows, filed, NoCatalogue, Likes.None);

        // No flavour: this test is about the genre filter, and a sub-genre preference would be a
        // second narrowing over rows whose tags say nothing (see MoodFlavour).
        var rock = shelf.Calm.Within(["Rock"], []).Songs;

        var rockInWhole = shelf.Calm.WholeList.Songs
            .Where(p => p.StartsWith("rock/", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(rock);
        Assert.All(rock, p => Assert.StartsWith("rock/", p, StringComparison.Ordinal));

        // The same songs, whatever order each list puts them in.
        Assert.Equal(
            rockInWhole.OrderBy(p => p, StringComparer.Ordinal),
            rock.OrderBy(p => p, StringComparer.Ordinal));
    }

    /// <summary>A genre with too few songs in a band does not get its own row. Four calm jazz
    /// records are four records, not a playlist.</summary>
    [Fact]
    public void A_genre_with_almost_nothing_in_it_is_not_worth_listing()
    {
        // Two hundred, so the rock band is well above LeastToShow. With fewer rows the margin would be
        // small, and the test would fail when MoodBands.CalmAt changes, which says nothing about the
        // eight-track genre this test is about.
        var rows = Ramp("rock", 200);
        for (int i = 0; i < 8; i++)
            rows.Add(Track("jazz", 0.001 * i, 0.001 * i, 0.5, 900 + i));

        var filed = FiledAs(rows, "Rock");
        filed["jazz"] = "Jazz";
        var shelf = MoodShelf.Build(rows, filed, NoCatalogue, Likes.None);

        var offered = shelf.Calm.GenresWorthListing(20);

        Assert.Contains("Rock", offered);
        Assert.DoesNotContain("Jazz", offered);
    }

    // ── sleep, the one list that is not cut within a genre ──

    /// <summary>
    /// The reason Sleep is measured on an absolute scale. On a shelf that is mostly loud dance music,
    /// the calmest dance records are calm FOR DANCE and belong in that list, but nobody can sleep to
    /// them. Sleep asks the question of the whole collection, so it takes the quiet records and leaves
    /// the ones that are only quieter.
    /// </summary>
    [Fact]
    public void Sleep_takes_the_quiet_records_not_the_quietest_loud_ones()
    {
        var rows = new List<MeasuredMood>();

        // A big, loud dance shelf. Its own quiet end is still busy and bright.
        for (int i = 0; i < 90; i++)
        {
            double at = (double)i / 89;
            rows.Add(Track("dance", 0.55 + at * 0.4, 7 + at * 6, 2 + at, i));
        }

        // A small, really still ambient shelf: dark, almost no events, and flat. The flatness matters
        // as much as the stillness: a drone with as much swing as the dance records would be rejected
        // by SleepSwingAt, which is the next test.
        // Ten, not twenty: the calm band is a share of the genre, and the twenty stillest records out
        // of a hundred would BE the whole band. The last assertion below would then have nothing to
        // find, and the test would depend on MoodBands.CalmAt, which is not what it is about.
        for (int i = 0; i < 10; i++)
        {
            double at = (double)i / 9;
            rows.Add(Track("ambient", 0.02 + at * 0.05, 0.2 + at * 0.4, 1 + at * 0.5, 500 + i));
        }

        var filed = Filed(("dance", "Electronic / Dance"), ("ambient", "Electronic / Dance"));
        var shelf = MoodShelf.Build(rows, filed, NoCatalogue, Likes.None);

        Assert.NotEmpty(shelf.Sleep.WholeList.Songs);
        Assert.All(shelf.Sleep.WholeList.Songs,
            p => Assert.StartsWith("ambient/", p, StringComparison.Ordinal));

        // The calm list, cut within the genre, does contain dance records. That is correct, and is
        // exactly the difference between the two questions.
        Assert.Contains(shelf.Calm.WholeList.Songs, p => p.StartsWith("dance/", StringComparison.Ordinal));
    }

    /// <summary>A record that is quiet and still but swings hard is rejected. A nocturne with a
    /// fortissimo in it measures as perfect sleep music on onsets and brightness alone, and it will
    /// wake somebody up.</summary>
    [Fact]
    public void Sleep_turns_away_a_quiet_record_that_swings()
    {
        var rows = new List<MeasuredMood>();
        for (int i = 0; i < 80; i++)
        {
            double at = (double)i / 79;
            rows.Add(Track("shelf", 0.2 + at * 0.7, 2 + at * 8, 4 + at * 6, i));
        }

        // A quiet record of its own, so it reaches the share a record must have. Three of its four
        // are still, dark and even; the fourth is just as still and swings harder than anything here.
        var loud = Track("quiet", 0.01, 0.1, 40, 900);
        var soft = Track("quiet", 0.01, 0.1, 4, 901);
        rows.Add(loud);
        rows.Add(soft);
        rows.Add(Track("quiet", 0.012, 0.11, 4.1, 902));
        rows.Add(Track("quiet", 0.013, 0.12, 4.2, 903));

        var shelf = MoodShelf.Build(rows, Filed(("shelf", "Classical"), ("quiet", "Classical")), NoCatalogue, Likes.None);

        Assert.Contains(soft.RelativePath, shelf.Sleep.WholeList.Songs);
        Assert.DoesNotContain(loud.RelativePath, shelf.Sleep.WholeList.Songs);
    }

    // ── the three things the measurement cannot decide ──

    /// <summary>
    /// A record is judged as a record. Four tracks of a loud album can measure as the stillest tracks
    /// on the shelf and still be ambient interludes between its songs (for example on a Blank Banshee
    /// album). Every number about those tracks is right, but they do not belong in the sleep
    /// list.
    /// </summary>
    [Fact]
    public void A_still_track_on_a_loud_record_is_not_sleep_music()
    {
        var rows = new List<MeasuredMood>();
        for (int i = 0; i < 80; i++)
        {
            double at = (double)i / 79;
            rows.Add(Track("loud", 0.4 + at * 0.5, 5 + at * 6, 5 + at * 3, i));
        }

        // Four interludes on that same record: as still, dark and flat as anything measured.
        var interludes = new List<MeasuredMood>();
        for (int i = 0; i < 4; i++)
        {
            var t = Track("loud", 0.01, 0.05, 1, 900 + i);
            interludes.Add(t);
            rows.Add(t);
        }

        // And a record that IS sleep music, most of the way through.
        for (int i = 0; i < 8; i++)
            rows.Add(Track("still", 0.02, 0.06, 1.2, 700 + i));

        var shelf = MoodShelf.Build(rows, Filed(("loud", "Rock"), ("still", "Classical")), NoCatalogue, Likes.None);

        Assert.All(interludes, t => Assert.DoesNotContain(t.RelativePath, shelf.Sleep.WholeList.Songs));
        Assert.Contains(shelf.Sleep.WholeList.Songs, p => p.StartsWith("still/", StringComparison.Ordinal));
    }

    /// <summary>A genre that is never sleep music is kept out however still it measures. The meter
    /// hears level and events but cannot hear a voice, which is why this rule is a list of genres and
    /// not a number.</summary>
    [Fact]
    public void A_genre_that_is_never_sleep_music_is_kept_out()
    {
        var rows = new List<MeasuredMood>();
        for (int i = 0; i < 60; i++)
        {
            double at = (double)i / 59;
            rows.Add(Track("loud", 0.4 + at * 0.5, 5 + at * 6, 5 + at * 3, i));
        }
        for (int i = 0; i < 8; i++)
            rows.Add(Track("rap", 0.02, 0.06, 1.2, 700 + i));
        for (int i = 0; i < 8; i++)
            rows.Add(Track("piano", 0.02, 0.06, 1.2, 800 + i));

        var shelf = MoodShelf.Build(rows, Filed(("loud", "Rock"), ("rap", "Hip-Hop / Rap"), ("piano", "Classical")), NoCatalogue, Likes.None);

        Assert.DoesNotContain(shelf.Sleep.WholeList.Songs, p => p.StartsWith("rap/", StringComparison.Ordinal));
        Assert.Contains(shelf.Sleep.WholeList.Songs, p => p.StartsWith("piano/", StringComparison.Ordinal));
    }

    /// <summary>A track with the explicit flag is kept out. This is not about the sound, but it is
    /// not what anybody wants in this list.</summary>
    [Fact]
    public void An_explicit_track_is_kept_out_of_the_sleep_list()
    {
        var rows = new List<MeasuredMood>();
        for (int i = 0; i < 60; i++)
        {
            double at = (double)i / 59;
            rows.Add(Track("loud", 0.4 + at * 0.5, 5 + at * 6, 5 + at * 3, i));
        }

        // One record, eight still songs, three of them flagged.
        for (int i = 0; i < 8; i++)
            rows.Add(Track("quiet", 0.02, 0.06, 1.2, 700 + i, explicitLyrics: i < 3));

        var shelf = MoodShelf.Build(rows, Filed(("loud", "Rock"), ("quiet", "Folk")), NoCatalogue, Likes.None);

        Assert.DoesNotContain("quiet/700.flac", shelf.Sleep.WholeList.Songs);
        Assert.DoesNotContain("quiet/701.flac", shelf.Sleep.WholeList.Songs);
        Assert.Contains("quiet/703.flac", shelf.Sleep.WholeList.Songs);
    }

    /// <summary>
    /// The sleep list is read from the top and stops at fifty, so it is ordered one record at a time,
    /// not strictly by stillness. Ranked strictly on a real collection, the first fifty can be
    /// fifteen tracks by one artist, which is the opposite of what a short list is for.
    /// </summary>
    [Fact]
    public void The_sleep_list_takes_a_turn_from_each_record()
    {
        var rows = new List<MeasuredMood>();
        for (int i = 0; i < 60; i++)
        {
            double at = (double)i / 59;
            rows.Add(Track("loud", 0.4 + at * 0.5, 5 + at * 6, 5 + at * 3, i));
        }

        // Three quiet records. The first is stiller than the others all the way through, so a strict
        // ranking would put every one of its songs before any of theirs.
        for (int i = 0; i < 6; i++)
        {
            rows.Add(Track("stillest", 0.010, 0.05, 1.0, 100 + i));
            rows.Add(Track("middling", 0.014, 0.07, 1.4, 200 + i));
            rows.Add(Track("last", 0.018, 0.09, 1.8, 300 + i));
        }

        var shelf = MoodShelf.Build(rows, Filed(
            ("loud", "Rock"), ("stillest", "Classical"), ("middling", "Classical"), ("last", "Classical")), NoCatalogue, Likes.None);

        // The LIST the user gets, which is where the record cap applies.
        var sleep = shelf.Sleep.WholeList.Window(SmartPlaylists.MostSongs(SmartPlaylistKind.Sleep), 0);
        var first3 = sleep.Take(3).Select(p => p.Split('/')[0]).ToList();

        // One from each record before any record gets a second turn, and the stillest is still first.
        Assert.Equal(["stillest", "middling", "last"], first3);

        // Three, not eighteen: the record cap limits every list to ONE song from an album, this one too.
        Assert.Equal(3, sleep.Count);

        // All eighteen are still in the pool, so a later reading can offer a different song from each
        // record, not the same three every time.
        Assert.Equal(18, shelf.Sleep.WholeList.Count);
    }

    /// <summary>Every mood list stops at fifty; the lists about listening stop at two hundred. A mood
    /// list is played in the background, not browsed, and fifty well-chosen songs are better than two
    /// hundred.</summary>
    [Fact]
    public void Every_mood_list_has_the_shorter_ceiling()
    {
        foreach (var kind in new[] { SmartPlaylistKind.Calm, SmartPlaylistKind.Energetic,
                                     SmartPlaylistKind.Danceable, SmartPlaylistKind.Sleep })
            Assert.Equal(50, SmartPlaylists.MostSongs(kind));

        Assert.Equal(SmartPlaylists.MaxTracks, SmartPlaylists.MostSongs(SmartPlaylistKind.MostPlayed));
    }

    // ── how many songs from one record, and from one act, a list may hold ──

    /// <summary>
    /// Why the caps exist: a mood is measured per track, and a record is made in one session, so a
    /// whole album engineered the same way lands in one band together, and a strictly ranked list would
    /// be that album with gaps in it.
    /// </summary>
    [Fact]
    public void Only_one_song_comes_off_one_record()
    {
        var rows = new List<MeasuredMood>();

        // One record whose twelve songs are the calmest songs on the shelf.
        for (int i = 0; i < 12; i++)
            rows.Add(Track("one-album", 0.01, 0.05, 1, 100 + i, artist: "One Act"));

        // And a hundred others after them.
        for (int i = 0; i < 100; i++)
        {
            double at = (double)i / 99;
            rows.Add(Track($"other-{i}", 0.1 + at * 0.8, 1 + at * 9, 2 + at * 8, i, artist: $"Act {i}"));
        }

        var shelf = MoodShelf.Build(rows, Filed(("one-album", "Rock")), NoCatalogue, Likes.None);

        // One LIST, which is where the cap applies. The pool keeps all twelve so a later reading can
        // offer a different one (see MoodPool).
        var calm = shelf.Calm.WholeList.Window(SmartPlaylists.MostSongs(SmartPlaylistKind.Calm), 0);

        // One, not twelve and not two. With two per record, a fifty-song list would still play
        // the same album twice from top to bottom.
        Assert.Equal(1, calm.Count(p => p.StartsWith("one-album/", StringComparison.Ordinal)));
    }

    /// <summary>The artist cap is looser: hearing an act again in a long list is acceptable, hearing
    /// the same album is not. So one act may give five songs, from five records.</summary>
    [Fact]
    public void At_most_five_songs_come_from_one_act()
    {
        var rows = new List<MeasuredMood>();

        // One act, ten records, two calm songs on each: under the record cap and over the artist one.
        for (int r = 0; r < 10; r++)
            for (int i = 0; i < 2; i++)
                rows.Add(Track($"prolific-{r}", 0.01, 0.05, 1, (r * 10) + i, artist: "Prolific"));

        for (int i = 0; i < 100; i++)
        {
            double at = (double)i / 99;
            rows.Add(Track($"other-{i}", 0.1 + at * 0.8, 1 + at * 9, 2 + at * 8, i, artist: $"Act {i}"));
        }

        var shelf = MoodShelf.Build(rows, Filed(), NoCatalogue, Likes.None);
        var calm = shelf.Calm.WholeList.Window(SmartPlaylists.MostSongs(SmartPlaylistKind.Calm), 0);

        Assert.Equal(5, calm.Count(p => p.StartsWith("prolific-", StringComparison.Ordinal)));
    }

    /// <summary>An untagged shelf is not one act with a thousand songs. Without this, the artist cap
    /// would limit a library with no artist credits to five songs in total.</summary>
    [Fact]
    public void Songs_with_no_credit_are_not_all_the_same_act()
    {
        var rows = new List<MeasuredMood>();
        for (int r = 0; r < 10; r++)
            rows.Add(Track($"nameless-{r}", 0.01, 0.05, 1, r, artist: ""));
        for (int i = 0; i < 100; i++)
        {
            double at = (double)i / 99;
            rows.Add(Track($"other-{i}", 0.1 + at * 0.8, 1 + at * 9, 2 + at * 8, i, artist: $"Act {i}"));
        }

        var shelf = MoodShelf.Build(rows, Filed(), NoCatalogue, Likes.None);

        Assert.True(shelf.Calm.WholeList.Songs.Count(p => p.StartsWith("nameless-", StringComparison.Ordinal)) > 5);
    }

    // ── the window that makes one list different next time ──

    // ── what a pooled list leaves out ──

    /// <summary>
    /// The main Chill list contains no rap, and its own hip-hop ROW contains nothing else.
    ///
    /// <para>Rap does not fit the main chill list. The exception is the whole rule: a list that says
    /// what it is about may contain what it says, and "Just the Bars" is about exactly this
    /// music.</para>
    /// </summary>
    [Fact]
    public void The_pooled_chill_list_leaves_the_rapping_out_and_the_hip_hop_row_keeps_it()
    {
        var rows = new List<MeasuredMood>();
        for (var i = 0; i < 20; i++)
            rows.Add(Track("quiet-rap", 0.01, 0.05, 1, i, artist: $"MC {i}"));
        for (var i = 0; i < 20; i++)
            rows.Add(Track("quiet-rock", 0.01, 0.05, 1, i, artist: $"Band {i}"));
        for (var i = 0; i < 100; i++)
        {
            var at = (double)i / 99;
            rows.Add(Track($"loud-{i}", 0.2 + at * 0.7, 2 + at * 8, 3 + at * 7, i, artist: $"Loud {i}"));
        }

        var shelf = MoodShelf.Build(rows, Filed(("quiet-rap", "Hip-Hop / Rap"), ("quiet-rock", "Rock")), NoCatalogue, Likes.None);

        Assert.DoesNotContain(shelf.Calm.WholeList.Songs,
            p => p.StartsWith("quiet-rap/", StringComparison.Ordinal));
        Assert.Contains(shelf.Calm.WholeList.Songs,
            p => p.StartsWith("quiet-rock/", StringComparison.Ordinal));

        // The row that is ABOUT it still has all of it.
        Assert.All(shelf.Calm.Within(["Hip-Hop / Rap"], []).Songs,
            p => Assert.StartsWith("quiet-rap/", p, StringComparison.Ordinal));
        Assert.NotEmpty(shelf.Calm.Within(["Hip-Hop / Rap"], []).Songs);
    }

    /// <summary>Every shelf this music is split across is named, not only the compound "Hip-Hop / Rap"
    /// shelf: a record tagged "Trap" is the same case.</summary>
    [Theory]
    [InlineData("Rap")]
    [InlineData("Trap")]
    [InlineData("Old School Hip Hop")]
    public void The_newer_rap_shelves_are_left_out_of_the_pooled_chill_list_too(string genre)
    {
        var rows = new List<MeasuredMood>();
        for (var i = 0; i < 20; i++)
            rows.Add(Track("quiet-rap", 0.01, 0.05, 1, i, artist: $"MC {i}"));
        for (var i = 0; i < 100; i++)
        {
            var at = (double)i / 99;
            rows.Add(Track($"loud-{i}", 0.2 + at * 0.7, 2 + at * 8, 3 + at * 7, i, artist: $"Loud {i}"));
        }

        var shelf = MoodShelf.Build(rows, Filed(("quiet-rap", genre)), NoCatalogue, Likes.None);

        Assert.DoesNotContain(shelf.Calm.WholeList.Songs,
            p => p.StartsWith("quiet-rap/", StringComparison.Ordinal));
    }

    /// <summary>
    /// The main Focus list contains no rap either, and its own rap ROWS still do.
    ///
    /// <para>This list is for music that stays in the background, and a voice talking to the listener
    /// cannot do that. On a real collection the veto takes 278 of 2,068 focused tracks out of the
    /// pooled list and leaves 1,790, which is still more than the fifty a list may show.</para>
    /// </summary>
    [Theory]
    [InlineData("Hip-Hop / Rap")]
    [InlineData("Rap")]
    [InlineData("Trap")]
    [InlineData("Old School Hip Hop")]
    public void The_pooled_focus_list_leaves_the_rapping_out_and_its_own_row_keeps_it(string genre)
    {
        var rows = new List<MeasuredMood>();

        // Sparse over a steady level, which is what focus is made of, on two shelves.
        for (var i = 0; i < 20; i++)
            rows.Add(Track("even-rap", 0.5, 0.05, 1, i, artist: $"MC {i}"));
        for (var i = 0; i < 20; i++)
            rows.Add(Track("even-rock", 0.5, 0.05, 1, i, artist: $"Band {i}"));
        for (var i = 0; i < 100; i++)
        {
            var at = (double)i / 99;
            rows.Add(Track($"busy-{i}", 0.2 + at * 0.7, 2 + at * 8, 3 + at * 7, i, artist: $"Loud {i}"));
        }

        var shelf = MoodShelf.Build(rows, Filed(("even-rap", genre), ("even-rock", "Rock")), NoCatalogue, Likes.None);

        Assert.DoesNotContain(shelf.Focus.WholeList.Songs,
            p => p.StartsWith("even-rap/", StringComparison.Ordinal));
        Assert.Contains(shelf.Focus.WholeList.Songs,
            p => p.StartsWith("even-rock/", StringComparison.Ordinal));

        // The row that is ABOUT it keeps all of it: "Lo-fi Beats" belongs on its own row.
        Assert.NotEmpty(shelf.Focus.Within([genre], []).Songs);
        Assert.All(shelf.Focus.Within([genre], []).Songs,
            p => Assert.StartsWith("even-rap/", p, StringComparison.Ordinal));
    }

    /// <summary>Only the quiet list and the focus list. Rap belongs in the loud and the danceable
    /// lists, so the veto is decided per mood and not applied to all of them.</summary>
    [Fact]
    public void The_loud_list_still_pools_the_rapping()
    {
        var rows = new List<MeasuredMood>();
        for (var i = 0; i < 20; i++)
            rows.Add(Track("loud-rap", 0.99, 9.5, 9, i, artist: $"MC {i}"));
        for (var i = 0; i < 100; i++)
        {
            var at = (double)i / 99;
            rows.Add(Track($"other-{i}", 0.05 + at * 0.7, 0.5 + at * 8, 1 + at * 7, i, artist: $"Act {i}"));
        }

        var shelf = MoodShelf.Build(rows, Filed(("loud-rap", "Hip-Hop / Rap")), NoCatalogue, Likes.None);

        Assert.Contains(shelf.Energetic.WholeList.Songs,
            p => p.StartsWith("loud-rap/", StringComparison.Ordinal));
    }

    // ── the window, and the caps that are taken inside it ──

    /// <summary>A pool of calm songs, one per record and one act each, so the caps never apply and the
    /// test checks only the window's own arithmetic.</summary>
    private static MoodPool CalmPool(int howMany)
    {
        var rows = new List<MeasuredMood>();
        for (var i = 0; i < howMany; i++)
            rows.Add(Track($"rec-{i:D3}", 0.01, 0.05, 1, 0, artist: $"Act {i}"));

        // A spread of louder music after them. Otherwise every track is in the calm band and the
        // percentile that defines "calm" has nothing to compare against.
        for (var i = 0; i < 100; i++)
        {
            var at = (double)i / 99;
            rows.Add(Track($"loud-{i}", 0.2 + at * 0.7, 2 + at * 8, 3 + at * 7, i, artist: $"Loud {i}"));
        }

        return MoodShelf.Build(rows, Filed(), NoCatalogue, Likes.None).Calm.WholeList;
    }

    [Fact]
    public void The_window_moves_through_the_pool()
    {
        var pool = CalmPool(5);

        var first = pool.Window(3, 0);
        var later = pool.Window(3, 2);

        Assert.Equal(3, first.Count);
        Assert.Equal([.. pool.Songs.Take(3)], first);
        Assert.Equal([.. pool.Songs.Skip(2).Take(3)], later);
    }

    /// <summary>The window wraps, so a pool smaller than the list gives the same songs in a different
    /// order, not a short list.</summary>
    [Fact]
    public void The_window_wraps_rather_than_running_out()
    {
        var pool = CalmPool(6);
        var songs = pool.Songs;
        var size = songs.Count;

        // Read the whole pool from two songs in: the same songs, rotated, not a short list.
        Assert.Equal([.. songs.Skip(2).Concat(songs.Take(2))], pool.Window(size, 2));

        // An offset past the end is the same as the offset it wraps onto.
        Assert.Equal(pool.Window(size, 1), pool.Window(size, 1 + size));
    }

    /// <summary>The number of songs returned never depends on the start position. That lets the pane's
    /// subtitle and the list agree while showing different songs.</summary>
    [Fact]
    public void Where_the_window_starts_does_not_change_how_long_it_is()
    {
        var pool = CalmPool(7);

        foreach (var from in new[] { 0, 1, 3, 6, 40 })
            Assert.Equal(4, pool.Window(4, from).Count);

        // A list longer than the pool is the whole pool, wherever it starts.
        Assert.Equal(pool.Count, pool.Window(500, 1).Count);
        Assert.Equal(pool.Count, pool.Window(500, 4).Count);
        Assert.Empty(MoodShelf.Build([], Filed(), NoCatalogue, Likes.None).Calm.WholeList.Window(50, 3));
    }

    /// <summary>
    /// The caps are a rule about ONE list, not about the pool: a second reading may take a different
    /// song from a record the first one used, which is what makes refresh useful.
    ///
    /// <para>If the caps were applied to the pool, the album's other songs could never be reached and
    /// every refresh would be the same songs in a different order (see <see cref="MoodPool"/>).</para>
    /// </summary>
    [Fact]
    public void A_later_window_may_take_a_different_song_off_the_same_record()
    {
        var rows = new List<MeasuredMood>();

        // One record, four calm songs on it, and nothing else calm to dilute them.
        for (var i = 0; i < 4; i++)
            rows.Add(Track("one-album", 0.01, 0.05, 1, 100 + i, artist: "One Act"));

        for (var i = 0; i < 100; i++)
        {
            var at = (double)i / 99;
            rows.Add(Track($"loud-{i}", 0.2 + at * 0.7, 2 + at * 8, 3 + at * 7, i, artist: $"Loud {i}"));
        }

        var pool = MoodShelf.Build(rows, Filed(), NoCatalogue, Likes.None).Calm.WholeList;

        // All four are still eligible: the pool keeps them.
        Assert.Equal(4, pool.Songs.Count(p => p.StartsWith("one-album/", StringComparison.Ordinal)));

        // ...any one list takes only one of them, wherever it starts...
        static string OffTheAlbum(IReadOnlyList<string> list) =>
            list.Single(p => p.StartsWith("one-album/", StringComparison.Ordinal));

        // ...and a window that starts after the one the first list took reaches a different one. The
        // offset is read from the pool, not guessed: the album's songs are ranked among the others,
        // not grouped at the front.
        var firstAt = pool.Songs
            .Select((path, at) => (path, at))
            .First(t => t.path.StartsWith("one-album/", StringComparison.Ordinal)).at;

        Assert.NotEqual(
            OffTheAlbum(pool.Window(50, firstAt)),
            OffTheAlbum(pool.Window(50, firstAt + 1)));
    }
}

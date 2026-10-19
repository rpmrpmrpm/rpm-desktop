using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// The recipe that turns three measurements into a word.
///
/// <para>Tested against made-up collections, not real ones, for the same reason
/// <see cref="MoodMeterTests"/> uses made-up signals: the properties of a made-up collection are known.
/// A shelf of forty identical records has no ends; a shelf that is mostly one genre and a little of
/// another has exactly the one property this class exists to handle.</para>
/// </summary>
public class MoodBandsTests
{
    /// <summary>A measured track on a record. The numbers are the meter's three, in its order.
    ///
    /// <para>The year is zero throughout: the bands are cut on how a record sounds, not on when it was
    /// made, so nothing in this file depends on the year. Narrowing to an era happens a step later, in
    /// <c>MoodList.InOneEra</c>, and has its own tests.</para></summary>
    /// <param name="lufs">How loud the master is, or null for a track whose loudness is not measured.
    /// Null leaves the other three numbers to decide, so <c>MoodBands.LoudAt</c> has no
    /// effect.</param>
    private static MeasuredMood Track(
        string folder, double brightness, double onsets, double dynamics, int n = 0,
        double? lufs = null) =>
        new($"{folder}/{n}.flac", folder, brightness, onsets, dynamics,
            IsExplicit: false, Artist: "", Year: 0, LyricValence: null, Loudness: lufs);

    /// <summary>A record's worth of tracks whose numbers rise evenly from zero to one, so the
    /// percentile of each one is known in advance.</summary>
    private static List<MeasuredMood> Ramp(string folder, int count)
    {
        var rows = new List<MeasuredMood>(count);
        for (int i = 0; i < count; i++)
        {
            double at = count == 1 ? 0 : (double)i / (count - 1);
            rows.Add(Track(folder, at, at, at, i));
        }
        return rows;
    }

    private static Dictionary<string, string> Filed(params (string Folder, string Genre)[] pairs)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, genre) in pairs)
            map[folder] = genre;
        return map;
    }

    // ── focus: the other half of the evenness axis ──

    /// <summary>Sparse over a steady level. The opposite of danceable, and never the same track:
    /// one needs busy and the other needs sparse, and the two ranges do not overlap.</summary>
    [Fact]
    public void Sparse_and_even_is_focused_and_busy_and_even_is_danceable()
    {
        var shelf = Ramp("rock", 100);

        // A ramp moves all three together, so nothing on it is even AND at an onset extreme. These two
        // are placed by hand against that shelf: the same steady level, opposite amounts of activity.
        var sparse = Track("rock", 0.5, 0.02, 0.02, 900);
        var busy = Track("rock", 0.5, 0.98, 0.02, 901);
        var filed = Filed(("rock", "Rock"));
        var bands = MoodBands.Cut([.. shelf, sparse, busy], filed);

        var quiet = bands.LabelFor(sparse, filed);
        var loud = bands.LabelFor(busy, filed);

        Assert.True(quiet.Focused);
        Assert.False(quiet.Danceable);
        Assert.True(loud.Danceable);
        Assert.False(loud.Focused);
    }

    /// <summary>
    /// The two lists never contain the same track, and TWO things keep that true.
    ///
    /// <para>On the onset readings it follows from the numbers: focus needs onsets under a third and
    /// danceable needs over two thirds, so no reading satisfies both. But <c>MoodBands.LoudAt</c>
    /// gives danceable a second way in: a loud, even track is danceable however few its onsets, and
    /// the same fact excludes it from focus. So the guarantee also depends on that exclusion, and an
    /// edit that removes it would put a track in both lists without changing either onset
    /// constant.</para>
    /// </summary>
    [Fact]
    public void No_track_is_both_focused_and_danceable()
    {
        var shelf = Ramp("rock", 200);
        var filed = Filed(("rock", "Rock"));

        // A track that reaches danceable through loudness: sparse, even, and mastered loud. Without
        // the loudness exclusion in focus, this one would satisfy both lists.
        var pushed = Track("rock", 0.5, 0.02, 0.02, 900, lufs: -5);
        var all = new List<MeasuredMood>([.. shelf, pushed]);
        var bands = MoodBands.Cut(all, filed);

        foreach (var track in all)
        {
            var label = bands.LabelFor(track, filed);
            Assert.False(label.Focused && label.Danceable);
        }

        Assert.True(bands.LabelFor(pushed, filed).Danceable);
    }

    // ── how loud the master is ──

    /// <summary>
    /// A loud half-time dance record must be danceable, not focus.
    ///
    /// <para>A half-time dance record has FEW onsets (its energy is sustained bass, not separate
    /// hits), and modern mastering leaves it no dynamic range, so on onsets and dynamics alone it reads
    /// as sparse and even, which is the definition of focus. Real examples measure about 3.7 onsets a
    /// second, against an electronic median of 4.71.</para>
    /// </summary>
    [Fact]
    public void A_sparse_even_track_mastered_loud_is_danceable_and_not_focused()
    {
        var shelf = Ramp("rock", 100);
        var filed = Filed(("rock", "Rock"));
        var pushed = Track("rock", 0.5, 0.02, 0.02, 900, lufs: MoodBands.LoudAt + 2);
        var bands = MoodBands.Cut([.. shelf, pushed], filed);

        var label = bands.LabelFor(pushed, filed);

        Assert.True(label.Danceable);
        Assert.False(label.Focused);
    }

    /// <summary>…and the same reading, mastered quietly, is still focus music. The condition must
    /// separate the two, not empty the list.</summary>
    [Fact]
    public void The_same_reading_mastered_quietly_is_still_focused()
    {
        var shelf = Ramp("rock", 100);
        var filed = Filed(("rock", "Rock"));
        var quiet = Track("rock", 0.5, 0.02, 0.02, 900, lufs: MoodBands.LoudAt - 8);
        var bands = MoodBands.Cut([.. shelf, quiet], filed);

        var label = bands.LabelFor(quiet, filed);

        Assert.True(label.Focused);
        Assert.False(label.Danceable);
    }

    /// <summary>A track with no loudness measurement stays exactly where its other three numbers put
    /// it. The loudness pass fills in a shelf over the minutes after an upgrade, and a rule that read
    /// "not measured" as "loud" would empty the focus list on every fresh install and then refill it.
    /// </summary>
    [Fact]
    public void A_track_with_no_loudness_reading_is_left_where_it_was()
    {
        var shelf = Ramp("rock", 100);
        var filed = Filed(("rock", "Rock"));
        var unmeasured = Track("rock", 0.5, 0.02, 0.02, 900);
        var bands = MoodBands.Cut([.. shelf, unmeasured], filed);

        var label = bands.LabelFor(unmeasured, filed);

        Assert.True(label.Focused);
        Assert.False(label.Danceable);
    }

    /// <summary>Loud is not enough on its own. Danceable still needs two conditions together, and a
    /// loud track whose level moves a lot is a rock band playing live, not a dance record.
    /// </summary>
    [Fact]
    public void Loud_but_uneven_is_not_danceable()
    {
        var shelf = Ramp("rock", 100);
        var filed = Filed(("rock", "Rock"));
        var swinging = Track("rock", 0.5, 0.02, 0.98, 900, lufs: MoodBands.LoudAt + 2);
        var bands = MoodBands.Cut([.. shelf, swinging], filed);

        var label = bands.LabelFor(swinging, filed);

        Assert.False(label.Danceable);
        Assert.False(label.Focused);
    }

    /// <summary>Focus is not a quieter calm: a track can be focused and at neither end of its genre.
    /// If focus were only the calm records, the row would be a second name for an existing one.
    /// </summary>
    [Fact]
    public void A_track_can_be_focused_and_in_no_band()
    {
        var shelf = Ramp("rock", 100);
        var middling = Track("rock", 0.5, 0.02, 0.02, 900);
        var filed = Filed(("rock", "Rock"));
        var bands = MoodBands.Cut([.. shelf, middling], filed);

        var label = bands.LabelFor(middling, filed);

        Assert.True(label.Focused);
        Assert.Equal(MoodBand.None, label.Band);
    }

    /// <summary>Stillness orders the focus list the way Drive orders the danceable one, and is zero
    /// where there is nothing to order.</summary>
    [Fact]
    public void Stillness_is_nought_unless_the_track_is_focused()
    {
        var shelf = Ramp("rock", 100);
        var still = Track("rock", 0.5, 0.0, 0.0, 900);
        var filed = Filed(("rock", "Rock"));
        var bands = MoodBands.Cut([.. shelf, still], filed);

        Assert.True(bands.LabelFor(still, filed).Stillness > 0);
        Assert.Equal(0, bands.LabelFor(shelf[^1], filed).Stillness);
    }

    // ── the ends, and the middle that is not a name ──

    [Fact]
    public void The_quiet_end_is_calm_and_the_busy_end_is_energetic()
    {
        var shelf = Ramp("rock", 100);
        var filed = Filed(("rock", "Rock"));
        var bands = MoodBands.Cut(shelf, filed);

        Assert.Equal(MoodBand.Calm, bands.LabelFor(shelf[0], filed).Band);
        Assert.Equal(MoodBand.Energetic, bands.LabelFor(shelf[^1], filed).Band);
    }

    /// <summary>Most of a collection is its middle, and the middle has no label. A recipe that
    /// labelled it would put every record in one list or the other.</summary>
    [Fact]
    public void The_middle_is_called_nothing()
    {
        var shelf = Ramp("rock", 100);
        var filed = Filed(("rock", "Rock"));
        var bands = MoodBands.Cut(shelf, filed);

        var middle = bands.LabelFor(shelf[50], filed);

        Assert.Equal(MoodBand.None, middle.Band);
        Assert.False(middle.Named);
        Assert.Equal(0, middle.Confidence);
    }

    /// <summary>A shelf of identical records has no ends. Every track ranks in the middle of its own
    /// tie, which gives no band (not an arbitrary quarter of them called calm).</summary>
    [Fact]
    public void A_shelf_that_is_all_one_thing_has_no_ends()
    {
        var shelf = new List<MeasuredMood>();
        for (int i = 0; i < 60; i++)
            shelf.Add(Track("rock", 0.4, 3, 9, i));
        var filed = Filed(("rock", "Rock"));

        var bands = MoodBands.Cut(shelf, filed);

        Assert.All(shelf, t => Assert.Equal(MoodBand.None, bands.LabelFor(t, filed).Band));
    }

    // ── the main design rule: cut within the genre ──

    /// <summary>
    /// The main reason for the design. A collection that is mostly loud electronic music with a little
    /// quiet classical, cut as one shelf, would call every classical record calm and every electronic
    /// one energetic: that only repeats the folder names. Cut within the genre, the quiet end of the
    /// classical is calm and the loud end of it is energetic, and the same is true for the
    /// electronic.
    /// </summary>
    [Fact]
    public void A_genre_is_cut_against_itself_and_not_against_the_shelf()
    {
        var shelf = new List<MeasuredMood>();

        // Loud, busy, compressed: a dance shelf, across the top half of everything.
        for (int i = 0; i < 60; i++)
        {
            double at = (double)i / 59;
            shelf.Add(Track("dance", 0.5 + at * 0.4, 6 + at * 6, 2 + at * 2, i));
        }

        // Quiet, still, wide: a classical shelf, across the bottom half of everything.
        for (int i = 0; i < 60; i++)
        {
            double at = (double)i / 59;
            shelf.Add(Track("classical", 0.05 + at * 0.2, 0.5 + at * 1.5, 14 + at * 6, i));
        }

        var filed = Filed(("dance", "Electronic / Dance"), ("classical", "Classical"));
        var bands = MoodBands.Cut(shelf, filed);

        var quietClassical = shelf.First(t => t.Folder == "classical");
        var loudClassical = shelf.Last(t => t.Folder == "classical");
        var quietDance = shelf.First(t => t.Folder == "dance");
        var loudDance = shelf.Last(t => t.Folder == "dance");

        // Both ends exist inside each genre, which a whole-shelf cut could never find.
        Assert.Equal(MoodBand.Calm, bands.LabelFor(quietClassical, filed).Band);
        Assert.Equal(MoodBand.Energetic, bands.LabelFor(loudClassical, filed).Band);
        Assert.Equal(MoodBand.Calm, bands.LabelFor(quietDance, filed).Band);
        Assert.Equal(MoodBand.Energetic, bands.LabelFor(loudDance, filed).Band);
    }

    /// <summary>The other half of the label. A band with no genre is "calm"; with a genre it is
    /// calm FOR ROCK, and the UI has the word it needs to say so.</summary>
    [Fact]
    public void The_label_carries_the_record_genre()
    {
        var shelf = Ramp("rock", 60);
        var filed = Filed(("rock", "Rock"));
        var bands = MoodBands.Cut(shelf, filed);

        Assert.Equal("Rock", bands.LabelFor(shelf[0], filed).Genre);
    }

    // ── a genre too thin to be its own distribution ──

    /// <summary>
    /// Nine jazz records are a shelf, not a distribution. Cut against each other, the band edge would
    /// depend on which few records happen to be in the library, so they are placed against the whole
    /// collection instead.
    /// </summary>
    [Fact]
    public void A_thin_genre_falls_back_to_the_whole_collection()
    {
        var shelf = Ramp("rock", 100);

        // Ten jazz tracks, each quieter and stiller than anything on the rock shelf.
        var jazz = new List<MeasuredMood>();
        for (int i = 0; i < 10; i++)
            jazz.Add(Track("jazz", 0.001 * i, 0.001 * i, 0.001 * i, i));
        shelf.AddRange(jazz);

        var filed = Filed(("rock", "Rock"), ("jazz", "Jazz"));
        var bands = MoodBands.Cut(shelf, filed);

        // Against the whole collection they are all at the bottom, so all of them are calm. Cut
        // against each other, the top of the ten would be energetic jazz.
        Assert.All(jazz, t => Assert.Equal(MoodBand.Calm, bands.LabelFor(t, filed).Band));
        Assert.Equal("Jazz", bands.LabelFor(jazz[^1], filed).Genre);
    }

    /// <summary>A record with no recognised genre is cut against the whole collection and labelled
    /// with no genre. Cutting unrecognised records against each other tells the listener nothing.</summary>
    [Fact]
    public void A_record_with_no_genre_is_cut_against_everything()
    {
        var shelf = Ramp("rock", 60);
        var loose = Track("loose", 0, 0, 0, 99);
        shelf.Add(loose);

        var filed = Filed(("rock", "Rock"));
        var bands = MoodBands.Cut(shelf, filed);

        var label = bands.LabelFor(loose, filed);

        Assert.Null(label.Genre);
        Assert.Equal(MoodBand.Calm, label.Band);
    }

    // ── danceable, which is separate from the band, not part of it ──

    /// <summary>Busy and even together. Either one alone is something else, and must not give
    /// danceable.</summary>
    [Fact]
    public void Danceable_wants_both_busy_and_even()
    {
        var shelf = new List<MeasuredMood>();
        for (int i = 0; i < 60; i++)
        {
            double at = (double)i / 59;
            // Onsets rise and dynamics rise with them, so the busiest tracks are also the widest.
            // That is a band playing live, and must not read as danceable.
            shelf.Add(Track("rock", at, at * 10, at * 20, i));
        }

        var filed = Filed(("rock", "Rock"));
        var bands = MoodBands.Cut(shelf, filed);

        Assert.All(shelf, t => Assert.False(bands.LabelFor(t, filed).Danceable));

        // The same shelf with one record that is busy and flat: many events over a level that
        // does not move.
        var steady = Track("rock", 0.8, 9.9, 0.1, 999);
        shelf.Add(steady);
        var again = MoodBands.Cut(shelf, filed);

        Assert.True(again.LabelFor(steady, filed).Danceable);
    }

    /// <summary>Danceable is separate from the band, not a third value of it: a record can be
    /// danceable without being at either end of its own genre.</summary>
    [Fact]
    public void Danceable_does_not_need_a_band()
    {
        var shelf = new List<MeasuredMood>();
        for (int i = 0; i < 60; i++)
        {
            double at = (double)i / 59;
            // Brightness runs the opposite way to onsets, so the middle of the arousal axis holds
            // busy tracks. Dynamics fall as onsets rise, so those same tracks are even.
            shelf.Add(Track("dance", 1 - at, at * 10, 20 - at * 20, i));
        }

        var filed = Filed(("dance", "Electronic / Dance"));
        var bands = MoodBands.Cut(shelf, filed);

        var busyAndEven = shelf.Single(t => t.RelativePath.EndsWith("/50.flac", StringComparison.Ordinal));
        var label = bands.LabelFor(busyAndEven, filed);

        Assert.Equal(MoodBand.None, label.Band);
        Assert.True(label.Danceable);
        Assert.True(label.Named);
    }

    // ── confidence ──

    /// <summary>A track at the very bottom is further into its band than one at the edge, and its
    /// confidence shows that. At the edge itself it is a 50/50 choice against the track just on the
    /// other side.</summary>
    [Fact]
    public void The_extreme_is_believed_more_than_the_edge()
    {
        var shelf = Ramp("rock", 400);
        var filed = Filed(("rock", "Rock"));
        var bands = MoodBands.Cut(shelf, filed);

        // The edge is a QUARTER of the way up the shelf, not a quarter of the way through its first
        // hundred: track 97 of 400 ranks at 0.244, just inside the calm band.
        double bottom = bands.LabelFor(shelf[0], filed).Confidence;
        double nearTheEdge = bands.LabelFor(shelf[97], filed).Confidence;

        Assert.True(bottom > nearTheEdge, $"bottom {bottom} should beat the edge {nearTheEdge}");
        Assert.True(nearTheEdge < 0.2, $"a track at the edge should not be believed: {nearTheEdge}");
    }

    /// <summary>Edges taken from forty tracks are less reliable than edges taken from four hundred,
    /// and the same reading from a smaller cohort has a lower confidence.</summary>
    [Fact]
    public void A_small_cohort_is_believed_less()
    {
        var small = Ramp("rock", MoodBands.LeastForOwnCut);
        var large = Ramp("rock", MoodBands.LeastForOwnCut * 4);
        var filed = Filed(("rock", "Rock"));

        double off40 = MoodBands.Cut(small, filed).LabelFor(small[0], filed).Confidence;
        double off160 = MoodBands.Cut(large, filed).LabelFor(large[0], filed).Confidence;

        Assert.True(off40 < off160, $"40 tracks {off40} should be believed less than 160 {off160}");
    }

    /// <summary>No band means no confidence. A number on a track that has no label would be read as
    /// a weak label, not as no label.</summary>
    [Fact]
    public void No_band_is_no_confidence()
    {
        var shelf = Ramp("rock", 100);
        var filed = Filed(("rock", "Rock"));
        var bands = MoodBands.Cut(shelf, filed);

        Assert.Equal(0, bands.LabelFor(shelf[50], filed).Confidence);
    }

    // ── the album's genre: the user's field first, then the catalogues ──

    private static readonly Dictionary<string, CatalogueFacts> NoFacts = new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, CatalogueFacts> Catalogued(string folder, string genre) =>
        new(StringComparer.OrdinalIgnoreCase) { [folder] = new CatalogueFacts(genre, []) };

    /// <summary>A record is filed under what most of its tracks say.</summary>
    [Fact]
    public void An_album_takes_the_genre_most_of_its_tracks_carry()
    {
        var filed = MoodBands.GenreByFolder([("rec", "Rock", 8), ("rec", "Jazz", 2)], NoFacts);

        Assert.Equal("Rock", filed["rec"]);
    }

    /// <summary>
    /// Only a tag that NAMES a shelf counts; a variant of one does not.
    ///
    /// <para>Genre is the user's field: one value, set by them, and it is not searched for words. So
    /// "Hard Rock", "Rock" and "Album Rock" are not combined into one rock record with nine tracks.
    /// Two of those three name no shelf, so the four-track jazz tag wins. A record whose tags all
    /// name no shelf falls through to what the catalogues say.</para>
    /// </summary>
    [Fact]
    public void Only_a_tag_that_names_a_shelf_is_counted()
    {
        var filed = MoodBands.GenreByFolder(
            [("rec", "Hard Rock", 3), ("rec", "Rock", 3), ("rec", "Album Rock", 3), ("rec", "Jazz", 4)],
            NoFacts);

        Assert.Equal("Jazz", filed["rec"]);
    }

    /// <summary>A tie gives a fixed answer. Otherwise a record would move between two reads of rows
    /// that did not change.</summary>
    [Fact]
    public void A_tie_is_broken_the_same_way_every_time()
    {
        var once = MoodBands.GenreByFolder([("rec", "Jazz", 5), ("rec", "Blues", 5)], NoFacts);
        var twice = MoodBands.GenreByFolder([("rec", "Blues", 5), ("rec", "Jazz", 5)], NoFacts);

        Assert.Equal("Blues", once["rec"]);
        Assert.Equal(once["rec"], twice["rec"]);
    }

    /// <summary>A tag nothing can place, with no catalogue answer, gives no genre, not a genre of its
    /// own. Such a record is cut against the whole collection, which is a real distribution (a group of
    /// unrecognised records is not).</summary>
    [Fact]
    public void A_tag_that_is_not_a_shelf_and_no_catalogue_is_no_genre()
    {
        Assert.Empty(MoodBands.GenreByFolder([("rec", "Norwegian Sea Shanties", 9)], NoFacts));
    }

    /// <summary>A tag that does not name a shelf directly is still read for one, where the vocabulary
    /// can place it: a style belongs to its own shelf, and a label with a slash is read the same way
    /// as in the rest of the app. Without this, the record would be cut against the whole collection
    /// even though its own tag says what it is, and in a real library that is most records.</summary>
    [Fact]
    public void A_tag_that_names_no_shelf_is_read_for_one()
    {
        Assert.Equal("Punk", MoodBands.GenreByFolder([("rec", "Post-Punk", 9)], NoFacts)["rec"]);
        Assert.Equal("R&B", MoodBands.GenreByFolder([("rec", "R&B / Soul", 9)], NoFacts)["rec"]);
    }

    /// <summary>The reading is the LAST answer, not a competitor to the first two: a tag that has to be
    /// read is a weaker answer than a catalogue's, so the catalogue still wins.</summary>
    [Fact]
    public void The_catalogue_outranks_a_tag_that_had_to_be_read()
    {
        var filed = MoodBands.GenreByFolder([("rec", "Post-Punk", 9)], Catalogued("rec", "Jazz"));

        Assert.Equal("Jazz", filed["rec"]);
    }

    /// <summary>A tag that NAMES a shelf still beats both, however many tracks have the read tag:
    /// the majority is taken within one tier and never across two.</summary>
    [Fact]
    public void A_named_shelf_outranks_a_read_one_however_the_counts_fall()
    {
        var filed = MoodBands.GenreByFolder(
            [("rec", "Jazz", 1), ("rec", "Post-Punk", 99)], NoFacts);

        Assert.Equal("Jazz", filed["rec"]);
    }

    /// <summary>A style is not a shelf, and the catalogue that knows this is a punk record says so.</summary>
    [Fact]
    public void The_catalogue_answers_where_the_users_field_did_not()
    {
        var filed = MoodBands.GenreByFolder([("rec", "Post-Punk", 9)], Catalogued("rec", "Punk"));

        Assert.Equal("Punk", filed["rec"]);
    }

    /// <summary>The owner's own tag beats the catalogue for their own copy of the record.</summary>
    [Fact]
    public void The_users_own_field_outranks_the_catalogue()
    {
        var filed = MoodBands.GenreByFolder([("rec", "Jazz", 9)], Catalogued("rec", "Punk"));

        Assert.Equal("Jazz", filed["rec"]);
    }

    /// <summary>A folder with no tracks tagged at all still takes the catalogue's answer.</summary>
    [Fact]
    public void A_folder_nobody_tagged_still_takes_the_catalogues_answer()
    {
        var filed = MoodBands.GenreByFolder([], Catalogued("rec", "Punk"));

        Assert.Equal("Punk", filed["rec"]);
    }

    /// <summary>A folder retagged since the cut is labelled by its current tag. The new genre may
    /// have no cohort of its own; that uses the fallback and is not a failure.</summary>
    [Fact]
    public void A_record_retagged_after_the_cut_is_labelled_by_what_it_says_now()
    {
        var shelf = Ramp("rock", 100);
        var atTheCut = Filed(("rock", "Rock"));
        var bands = MoodBands.Cut(shelf, atTheCut);

        var since = Filed(("rock", "Metal"));
        var label = bands.LabelFor(shelf[0], since);

        Assert.Equal("Metal", label.Genre);
        Assert.Equal(MoodBand.Calm, label.Band);
    }
}

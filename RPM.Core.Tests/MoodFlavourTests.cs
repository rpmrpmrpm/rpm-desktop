using RPM.Core.Library;
using RPM.Core.Metadata;

namespace RPM.Core.Tests;

/// <summary>
/// The third narrowing on a generated list, after the mood range and the genre: preferring the records
/// the catalogues filed under the styles the list is named after.
///
/// <para>It reads <see cref="CatalogueFacts"/> and never a typed tag. Genre is the user's field, one
/// value, and a user who sets theirs to "Rock" (which is correct) must not break their own lists by
/// doing so.</para>
/// </summary>
public class MoodFlavourTests
{
    private static MeasuredMood Row(string folder, int n, double at, int year = 1990) =>
        new($"{folder}/{n}.flac", folder, at, at, at, IsExplicit: false, Artist: $"act {n}", year,
            // No loudness: this file is about the flavour narrowing, not about loudness.
            LyricValence: null, Loudness: null);

    /// <summary>A ramp of tracks in one genre, one act each so no cap applies, split across two
    /// folders: one the catalogues described and one they did not.</summary>
    private static (List<MeasuredMood> Rows, Dictionary<string, string> Filed) Ramp(int described, int unknown)
    {
        var rows = new List<MeasuredMood>();
        var filed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int total = described + unknown, i = 0;

        for (var n = 0; n < described; n++, i++)
            rows.Add(Row("described", i, (double)i / (total - 1)));
        for (var n = 0; n < unknown; n++, i++)
            rows.Add(Row("unknown", i, (double)i / (total - 1)));

        filed["described"] = "Rock";
        filed["unknown"] = "Rock";
        return (rows, filed);
    }

    private static Dictionary<string, CatalogueFacts> Facts(params (string Folder, string[] Styles)[] pairs)
    {
        var map = new Dictionary<string, CatalogueFacts>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, styles) in pairs)
            map[folder] = new CatalogueFacts("Rock", styles);
        return map;
    }

    // ── the table must use the vocabulary ──

    [Fact]
    public void Every_style_the_table_names_is_a_style_the_app_knows()
    {
        // A name the vocabulary does not contain is a line that silently never matches. That is the
        // failure this table must avoid, so it is checked here.
        var unknown = MoodFlavour.EveryStyleNamed.Where(s => !Subgenres.Knows(s)).ToList();

        Assert.Empty(unknown);
    }

    [Fact]
    public void No_line_asks_for_the_shelf_it_is_already_on()
    {
        // "Rock" for a rock list matches every rock record and narrows nothing.
        foreach (var name in MoodFlavour.EveryStyleNamed)
            Assert.DoesNotContain(name, Genres.All, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_pair_with_no_line_asks_for_nothing()
    {
        Assert.Empty(MoodFlavour.Styles(SmartPlaylistKind.Calm, "Jazz"));
        Assert.Empty(MoodFlavour.Styles(SmartPlaylistKind.Focus, "Rock"));
        Assert.Empty(MoodFlavour.Styles(SmartPlaylistKind.Calm, null));
    }

    [Fact]
    public void A_pair_with_a_line_asks_by_name()
        => Assert.Contains("Soft Rock", MoodFlavour.Styles(SmartPlaylistKind.Calm, "Rock"));

    // ── the narrowing ──

    [Fact]
    public void A_list_prefers_the_records_the_catalogues_named()
    {
        // Enough described records to fill a list, so the narrowing is kept.
        var (rows, filed) = Ramp(described: 200, unknown: 200);
        var shelf = MoodShelf.Build(rows, filed, Facts(("described", ["Soft Rock"])), Likes.None);

        var songs = shelf.Calm.Within(["Rock"], MoodFlavour.Styles(SmartPlaylistKind.Calm, "Rock")).Songs;

        Assert.NotEmpty(songs);
        Assert.All(songs, s => Assert.StartsWith("described/", s, StringComparison.Ordinal));
    }

    [Fact]
    public void A_style_the_line_does_not_ask_for_is_not_preferred()
    {
        var (rows, filed) = Ramp(described: 200, unknown: 200);
        var shelf = MoodShelf.Build(rows, filed, Facts(("described", ["Death Metal"])), Likes.None);

        var narrowed = shelf.Calm.Within(["Rock"], MoodFlavour.Styles(SmartPlaylistKind.Calm, "Rock")).Songs;
        var loose = shelf.Calm.Within(["Rock"], []).Songs;

        Assert.Equal(loose, narrowed);
    }

    [Fact]
    public void Too_few_named_records_and_the_list_stays_as_it_was()
    {
        // A preference, never a filter: below MoodFlavour.LeastToPrefer the narrowed list would be
        // shorter than the one it replaces, which fits the name less well, not better.
        var (rows, filed) = Ramp(described: 4, unknown: 400);
        var shelf = MoodShelf.Build(rows, filed, Facts(("described", ["Soft Rock"])), Likes.None);

        var narrowed = shelf.Calm.Within(["Rock"], MoodFlavour.Styles(SmartPlaylistKind.Calm, "Rock")).Songs;
        var loose = shelf.Calm.Within(["Rock"], []).Songs;

        Assert.Equal(loose, narrowed);
    }

    [Fact]
    public void A_collection_no_catalogue_has_reached_is_not_narrowed_at_all()
    {
        // The normal state of a library the sweep has not reached yet. It must fall back to the
        // wider list, not to an empty one.
        var (rows, filed) = Ramp(described: 0, unknown: 400);
        var shelf = MoodShelf.Build(rows, filed, new Dictionary<string, CatalogueFacts>(), Likes.None);

        var narrowed = shelf.Calm.Within(["Rock"], MoodFlavour.Styles(SmartPlaylistKind.Calm, "Rock")).Songs;

        Assert.NotEmpty(narrowed);
        Assert.Equal(shelf.Calm.Within(["Rock"], []).Songs, narrowed);
    }

    [Fact]
    public void A_spelling_difference_is_not_a_miss()
    {
        // Both sides are folded, so a catalogue that writes "soft-rock" matches the line "Soft Rock".
        var (rows, filed) = Ramp(described: 200, unknown: 200);
        var shelf = MoodShelf.Build(rows, filed, Facts(("described", ["soft-rock"])), Likes.None);

        var songs = shelf.Calm.Within(["Rock"], MoodFlavour.Styles(SmartPlaylistKind.Calm, "Rock")).Songs;

        Assert.All(songs, s => Assert.StartsWith("described/", s, StringComparison.Ordinal));
    }

    [Fact]
    public void A_style_is_matched_whole_and_not_as_a_substring()
    {
        // The style is matched from a known list, not searched as text, so "rock" does not match
        // "Soft Rock", and a record filed under Punk Rock is not a soft rock record.
        var (rows, filed) = Ramp(described: 200, unknown: 200);
        var shelf = MoodShelf.Build(rows, filed, Facts(("described", ["Punk Rock"])), Likes.None);

        var narrowed = shelf.Calm.Within(["Rock"], MoodFlavour.Styles(SmartPlaylistKind.Calm, "Rock")).Songs;

        Assert.Equal(shelf.Calm.Within(["Rock"], []).Songs, narrowed);
    }
}

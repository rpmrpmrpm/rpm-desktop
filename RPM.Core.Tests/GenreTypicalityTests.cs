using RPM.Core.Library;
using RPM.Core.Metadata;

namespace RPM.Core.Tests;

/// <summary>
/// What a genre row prefers that the pooled list above it does not.
///
/// <para>"The calmest music I own" and "the calmest rock I own" are different questions. The second
/// wants the answer to be rock, so a genre row prefers the songs that sound like the middle of their
/// own genre. It uses whichever axis the band has not already used, because only then can the term
/// add information the band does not have.</para>
/// </summary>
public class GenreTypicalityTests
{
    private static MeasuredMood Row(
        string path, string folder, double brightness, double onsets, double dynamics) =>
        new(path, folder, brightness, onsets, dynamics,
            IsExplicit: false, Artist: path, Year: 1990, LyricValence: null,
            // Loudness was not measured, so the other three decide.
            Loudness: null);

    private static Dictionary<string, string> Filed(IEnumerable<MeasuredMood> rows, string genre) =>
        rows.Select(r => r.Folder).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(f => f, _ => genre, StringComparer.OrdinalIgnoreCase);

    private static MoodShelf Build(List<MeasuredMood> rows, string genre = "Rock") =>
        MoodShelf.Build(
            rows, Filed(rows, genre), new Dictionary<string, CatalogueFacts>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, IReadOnlySet<SmartPlaylistKind>>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// A shelf of one genre where thirty songs are EQUALLY calm, and differ only in dynamics.
    ///
    /// <para>Equal on purpose. Arousal is a percentile within the genre, so songs whose brightness and
    /// onsets differ at all spread across the whole band, and the bounded typicality term is a small
    /// fraction of that by design: it orders songs the band grouped together, and does not override
    /// the band. Thirty songs with identical brightness and onsets are such a group, and this is the
    /// case the feature exists for.</para>
    ///
    /// <para>Calm does not select on dynamics, so typicality reads that axis, and its values cover the
    /// whole range across the shelf so the genre's middle is a real middle.</para>
    /// </summary>
    private static List<MeasuredMood> OneGenre(int tied = 30, int rest = 170)
    {
        var rows = new List<MeasuredMood>();

        // The tied group: identical arousal, so the band gives every one of them the same confidence.
        for (var i = 0; i < tied; i++)
            rows.Add(Row($"rock/tied-{i:D3}.flac", $"rock-tied-{i:D3}", 0.01, 0.01, i / (tied - 1.0)));

        // The rest of the genre, louder and busier, so the tied group is in the calm band and the
        // dynamics values cover their whole range.
        for (var i = 0; i < rest; i++)
        {
            var arousal = 0.5 + (i / (rest * 2.0));
            rows.Add(Row($"rock/rest-{i:D3}.flac", $"rock-rest-{i:D3}", arousal, arousal, i / (rest - 1.0)));
        }

        return rows;
    }

    /// <summary>The dynamics value a song in the row was built with, read back from the fixture.</summary>
    private static double DynamicsOf(List<MeasuredMood> rows, string path) =>
        rows.Single(r => r.RelativePath == path).Dynamics;

    /// <summary>The purpose of the feature: the row starts with songs that are typical for the genre,
    /// not with its outliers.</summary>
    [Fact]
    public void A_genre_row_prefers_the_songs_that_sound_like_the_genre()
    {
        var rows = OneGenre();
        var shelf = Build(rows);

        var row = shelf.Calm.Within(["Rock"], MoodFlavour.Styles(SmartPlaylistKind.Calm, "Rock")).Songs;

        Assert.NotEmpty(row);

        // Dynamics goes from 0 to 1 across the shelf, so the genre's middle is around 0.5. Among songs
        // the band rated identically, the row starts with the one nearest that middle and ends with
        // one of the extremes.
        Assert.InRange(DynamicsOf(rows, row[0]), 0.35, 0.65);
        Assert.True(
            Math.Abs(DynamicsOf(rows, row[^1]) - 0.5) > Math.Abs(DynamicsOf(rows, row[0]) - 0.5),
            "the row should close further from the genre's middle than it opens");
    }

    /// <summary>The pooled list is NOT re-ranked. It answers the other question, and it keeps the order
    /// the measurements gave it.</summary>
    [Fact]
    public void The_pooled_list_is_not_re_ranked()
    {
        var rows = OneGenre();
        var shelf = Build(rows);

        var pooled = shelf.Calm.WholeList.Songs;
        var row = shelf.Calm.Within(["Rock"], MoodFlavour.Styles(SmartPlaylistKind.Calm, "Rock")).Songs;

        Assert.NotEqual(pooled, row);

        // With the band rating them all alike, the pooled list falls back to the path, which is the
        // order they were built in and says nothing about the genre.
        Assert.Equal("rock/tied-000.flac", pooled[0]);
    }

    /// <summary>Same songs, different order. The term may reorder a row and must never change which
    /// songs are in it.</summary>
    [Fact]
    public void A_genre_row_holds_the_same_songs_it_always_held()
    {
        var rows = OneGenre();
        var shelf = Build(rows);

        var pooled = shelf.Calm.WholeList.Songs;
        var row = shelf.Calm.Within(["Rock"], MoodFlavour.Styles(SmartPlaylistKind.Calm, "Rock")).Songs;

        Assert.Equal(
            pooled.OrderBy(p => p, StringComparer.Ordinal),
            row.OrderBy(p => p, StringComparer.Ordinal));
    }

    /// <summary>Two reads of one unchanged shelf return the same order.</summary>
    [Fact]
    public void Two_reads_of_one_genre_row_agree()
    {
        var rows = OneGenre();
        var shelf = Build(rows);
        var styles = MoodFlavour.Styles(SmartPlaylistKind.Calm, "Rock");

        Assert.Equal(shelf.Calm.Within(["Rock"], styles).Songs, shelf.Calm.Within(["Rock"], styles).Songs);
    }

    /// <summary>
    /// The term is bounded, so it cannot move a song outside the band it was picked from.
    ///
    /// <para>Here the arousal spread is wide, so the ranking is dominated by how calm a song is. The
    /// least calm song in the row must not reach the top of it however ordinary its dynamics are.
    /// </para>
    /// </summary>
    [Fact]
    public void Typicality_cannot_overturn_the_band()
    {
        var rows = new List<MeasuredMood>();
        for (var i = 0; i < 200; i++)
        {
            // Arousal covers the band, and dynamics is most typical for the LEAST calm song.
            var arousal = i / 2000.0;
            var dynamics = i == 199 ? 0.5 : (i < 100 ? 0.02 : 0.98);
            rows.Add(Row($"rock/{i:D3}.flac", $"rock-{i:D3}", arousal, arousal, dynamics));
        }

        var shelf = Build(rows);
        var row = shelf.Calm.Within(["Rock"], MoodFlavour.Styles(SmartPlaylistKind.Calm, "Rock")).Songs;

        Assert.NotEmpty(row);
        Assert.NotEqual("rock/199.flac", row[0]);
    }

    /// <summary>Sleep has no genre rows at all, so nothing here affects it: it is cut across the whole
    /// library, and "sleep jazz" is just the jazz that is in it.</summary>
    [Fact]
    public void Sleep_is_untouched()
    {
        Assert.DoesNotContain(SmartPlaylistKind.Sleep, SmartPlaylists.BrokenOutByGenre);
    }
}

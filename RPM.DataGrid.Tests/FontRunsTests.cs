using RPM.DataGrid.Text;

namespace RPM.DataGrid.Tests;

/// <summary>
/// Splitting a string into runs of one face each. The cases are the ones a music library actually
/// produces: a title in one script, a title in two, an artist with an accent, an emoji in a playlist
/// name.
/// </summary>
public class FontRunsTests
{
    /// <summary>
    /// A stand-in for the installed fonts. Each face is declared as the set of code-point ranges it
    /// covers, which is all <see cref="FontRuns"/> ever asks.
    /// </summary>
    private sealed class Faces : IFaceCoverage
    {
        private readonly List<(int Lo, int Hi)[]> _ranges = [];

        public int Primary => 0;

        /// <summary>Adds a face and returns its number. The first one added is the primary.</summary>
        public int Add(params (int Lo, int Hi)[] ranges)
        {
            _ranges.Add(ranges);
            return _ranges.Count - 1;
        }

        public bool Covers(int face, int codePoint)
        {
            if (face < 0 || face >= _ranges.Count)
                return false;
            foreach (var (lo, hi) in _ranges[face])
                if (codePoint >= lo && codePoint <= hi)
                    return true;
            return false;
        }

        public int Fallback(int codePoint)
        {
            for (var f = 0; f < _ranges.Count; f++)
                if (Covers(f, codePoint))
                    return f;
            return -1;
        }
    }

    /// <summary>ASCII plus Latin-1, which is what a plain UI face is being stood in for by.</summary>
    private static Faces Latin(out int latin)
    {
        var faces = new Faces();
        latin = faces.Add((0x0020, 0x024F));
        return faces;
    }

    private static List<FontRun> Split(string text, IFaceCoverage faces)
    {
        var runs = new List<FontRun>();
        FontRuns.Split(text, faces, runs);
        return runs;
    }

    private static string Text(string source, FontRun run) => source.Substring(run.Start, run.Length);

    [Fact]
    public void Empty_string_produces_no_runs()
    {
        var faces = Latin(out _);
        Assert.Empty(Split("", faces));
    }

    [Fact]
    public void One_script_is_one_run()
    {
        var faces = Latin(out var latin);
        var runs = Split("Blue Monday", faces);

        var run = Assert.Single(runs);
        Assert.Equal(latin, run.Face);
        Assert.Equal("Blue Monday", Text("Blue Monday", run));
    }

    [Fact]
    public void A_script_the_primary_cannot_draw_goes_to_the_fallback()
    {
        var faces = Latin(out var latin);
        var cjk = faces.Add((0x3000, 0x30FF), (0x4E00, 0x9FFF));

        const string title = "児安御所";   // 児安御所
        var run = Assert.Single(Split(title, faces));

        Assert.Equal(cjk, run.Face);
        Assert.NotEqual(latin, run.Face);
    }

    [Fact]
    public void A_mixed_title_splits_where_the_script_changes()
    {
        var faces = Latin(out var latin);
        var cjk = faces.Add((0x3000, 0x30FF), (0x4E00, 0x9FFF));

        const string title = "Kokoro の声 remix";
        var runs = Split(title, faces);

        Assert.Equal(3, runs.Count);
        Assert.Equal(latin, runs[0].Face);
        Assert.Equal("Kokoro ", Text(title, runs[0]));
        Assert.Equal(cjk, runs[1].Face);
        Assert.Equal("の声", Text(title, runs[1]));
        Assert.Equal(latin, runs[2].Face);
        Assert.Equal(" remix", Text(title, runs[2]));
    }

    /// <summary>
    /// The ordering rule in <c>FaceFor</c>. A space is covered by both faces, so asking the primary
    /// first would end the run at every space and start it again after it: six runs for a sentence
    /// instead of one.
    /// </summary>
    [Fact]
    public void A_space_inside_a_fallback_run_does_not_break_it()
    {
        var faces = Latin(out _);
        var cjk = faces.Add((0x0020, 0x0020), (0x3000, 0x30FF), (0x4E00, 0x9FFF));

        const string title = "東京 タワー 夕焼け";
        var run = Assert.Single(Split(title, faces));

        Assert.Equal(cjk, run.Face);
        Assert.Equal(title.Length, run.Length);
    }

    [Fact]
    public void Runs_cover_the_whole_string_with_no_gaps_or_overlaps()
    {
        var faces = Latin(out _);
        faces.Add((0x0400, 0x04FF));                       // Cyrillic
        faces.Add((0x4E00, 0x9FFF));                       // Han

        const string title = "Mix Москва 北京 2024";
        var runs = Split(title, faces);

        var at = 0;
        foreach (var run in runs)
        {
            Assert.Equal(at, run.Start);
            Assert.True(run.Length > 0);
            at += run.Length;
        }

        Assert.Equal(title.Length, at);
        Assert.Equal(title, string.Concat(runs.Select(r => Text(title, r))));
    }

    /// <summary>
    /// A combining acute after an "e" must not be handed a face of its own. Split it off and the
    /// shaper sees an accent with nothing to sit on, and it floats.
    /// </summary>
    [Fact]
    public void A_combining_mark_stays_with_the_letter_in_front_of_it()
    {
        var faces = Latin(out var latin);
        faces.Add((0x0300, 0x036F));                       // a face that does cover the mark

        const string name = "Björk é";          // the acute is combining
        var run = Assert.Single(Split(name, faces));

        Assert.Equal(latin, run.Face);
        Assert.Equal(name.Length, run.Length);
    }

    /// <summary>An emoji is one code point above the basic plane, and the modifier and joiner after
    /// it belong to it.</summary>
    [Fact]
    public void An_emoji_sequence_is_one_run()
    {
        var faces = Latin(out _);
        var emoji = faces.Add((0x1F000, 0x1FAFF), (0x200D, 0x200D));

        const string name = "👩‍🎤";   // woman + ZWJ + microphone
        var run = Assert.Single(Split(name, faces));

        Assert.Equal(emoji, run.Face);
        Assert.Equal(name.Length, run.Length);
    }

    /// <summary>
    /// Nothing installed draws it. The primary gets it, so the run stays whole and the character
    /// shows as .notdef, rather than the string being cut into pieces around a box.
    /// </summary>
    [Fact]
    public void A_character_no_face_covers_falls_to_the_primary()
    {
        var faces = Latin(out var latin);

        const string title = "Song \uE000 Two";       // private use area: nothing has a glyph
        var run = Assert.Single(Split(title, faces));

        Assert.Equal(latin, run.Face);
        Assert.Equal(title.Length, run.Length);
    }

    /// <summary>A lone high surrogate must still advance the reader. A truncated tag field can
    /// produce one and the grid must draw the rest of the line.</summary>
    [Fact]
    public void An_unpaired_surrogate_does_not_stop_the_split()
    {
        var faces = Latin(out _);

        var title = "A\uD83DB";
        var runs = Split(title, faces);

        Assert.Equal(title.Length, runs.Sum(r => r.Length));
        Assert.Equal(0, runs[0].Start);
    }

    [Fact]
    public void The_list_is_cleared_before_it_is_filled()
    {
        var faces = Latin(out _);
        var runs = new List<FontRun> { new(99, 99, 99) };

        FontRuns.Split("abc", faces, runs);

        Assert.Single(runs);
        Assert.Equal(0, runs[0].Start);
    }
}

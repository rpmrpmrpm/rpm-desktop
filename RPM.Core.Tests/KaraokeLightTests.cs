using RPM.Core.Lyrics;

namespace RPM.Core.Tests;

/// <summary>The karaoke light, as the Android TV app's KaraokeLightTest checks it.</summary>
public class KaraokeLightTests
{
    private const double Near = 1e-6;

    [Fact]
    public void A_letter_is_fully_lit_on_its_moment_and_unlit_a_softness_before()
    {
        // Not the last word, so it is not taken as held; 4 letters over 0.4 s is 0.1 s a letter.
        var word = new WordSpan(0, 4, 10.0, 10.4);
        Assert.Equal(1, KaraokeLight.Through(word, 0, last: false, now: 10.0, earliest: 10.0), Near);
        Assert.Equal(0, KaraokeLight.Through(word, 0, false, 10.0 - KaraokeLight.GlyphSoftness, 10.0), Near);
        Assert.Equal(0.5, KaraokeLight.Through(word, 0, false, 10.0 - (KaraokeLight.GlyphSoftness / 2), 10.0), Near);

        // The third letter opens two shares in, and arrives over the softness, which is wider than its share.
        Assert.Equal(1, KaraokeLight.Through(word, 2, false, 10.2, 10.0), Near);
        Assert.Equal(0.5, KaraokeLight.Through(word, 2, false, 10.1, 10.0), Near);
    }

    [Fact]
    public void The_light_moves_through_a_word_so_several_letters_are_part_lit()
    {
        var word = new WordSpan(0, 6, 5.0, 5.3);
        var at = Enumerable.Range(0, 6).Select(c => KaraokeLight.Through(word, c, false, 5.1, 5.0)).ToArray();

        for (var i = 1; i < at.Length; i++)
            Assert.True(at[i - 1] >= at[i], "the light reaches the letters in reading order");
        Assert.True(at.Count(a => a > 0 && a < 1) >= 3, "three or more letters are on their way at once");
    }

    [Fact]
    public void A_held_last_word_caught_only_at_its_tail_is_swept_through_the_hold()
    {
        // "it" ends at 85.5, then "on" measured as 89.28-89.32: the light goes through "on" over the hold,
        // not in 40 ms.
        var spans = new[] { new WordSpan(0, 2, 85.0, 85.5), new WordSpan(3, 2, 89.28, 89.32) };
        var into = new double[4];

        KaraokeLight.Light(spans, 86.3, into);
        Assert.True(into[2] > KaraokeLight.Rest && into[2] < 1);

        // The second letter is on its way, a second and a half behind the first.
        KaraokeLight.Light(spans, 87.0, into);
        Assert.True(into[3] > KaraokeLight.Rest && into[3] < 1);

        KaraokeLight.Light(spans, 89.32, into);
        Assert.Equal(1, into[3], Near);
    }

    [Fact]
    public void A_sliver_of_a_word_after_a_pause_eases_in_instead_of_snapping()
    {
        // Mid-line: "up" measured as 10.96-11.0 after a pause; swept over half a second before its end.
        var spans = new[]
        {
            new WordSpan(0, 2, 9.0, 9.5), new WordSpan(3, 2, 10.96, 11.0), new WordSpan(6, 3, 11.2, 11.6),
        };
        var into = new double[7];

        KaraokeLight.Light(spans, 10.2, into);
        Assert.Equal(KaraokeLight.Rest, into[2], Near);

        KaraokeLight.Light(spans, 10.6, into);
        Assert.True(into[2] > KaraokeLight.Rest && into[3] < 1);

        KaraokeLight.Light(spans, 11.0, into);
        Assert.Equal(1, into[3], Near);
    }

    [Fact]
    public void The_line_fades_back_to_rest_slowly_after_its_last_word()
    {
        var spans = new[] { new WordSpan(0, 2, 0.0, 0.5) };
        var into = new double[2];

        KaraokeLight.Light(spans, 0.5, into);
        Assert.Equal(1, into[1], Near);

        KaraokeLight.Light(spans, 0.5 + (KaraokeLight.LineFade / 2), into);
        Assert.Equal(KaraokeLight.Rest + ((1 - KaraokeLight.Rest) * 0.5), into[1], Near);

        KaraokeLight.Light(spans, 0.5 + KaraokeLight.LineFade, into);
        Assert.Equal(KaraokeLight.Rest, into[1], Near);

        var held = new double[2];
        KaraokeLight.Light(spans, 0.5 + (KaraokeLight.LineFade * 3), held, fades: false);
        Assert.All(held, h => Assert.Equal(1, h, Near));   // a line still being sung stays lit
    }

    [Fact]
    public void A_held_word_is_swept_slowly_over_its_hold()
    {
        // Two letters held for a second: half a second a letter, so the second letter is lit at 20.5 s.
        var word = new WordSpan(0, 2, 20.0, 21.0);
        Assert.Equal(1, KaraokeLight.Through(word, 0, last: true, now: 20.0, earliest: 20.0), Near);
        Assert.Equal(0, KaraokeLight.Through(word, 1, true, 20.0, 20.0), Near);
        Assert.Equal(0.5, KaraokeLight.Through(word, 1, true, 20.25, 20.0), Near);
        Assert.Equal(1, KaraokeLight.Through(word, 1, true, 20.5, 20.0), Near);
    }

    [Fact]
    public void A_stretched_word_is_crossed_no_slower_than_the_held_rate()
    {
        // Three letters measured over ten seconds: the aligner lost the thread. The last letter is lit by
        // two held letters' time.
        var word = new WordSpan(0, 3, 0.0, 10.0);
        Assert.Equal(1, KaraokeLight.Through(word, 2, false, 2 * KaraokeLight.HeldGlyph, 0.0), Near);
    }

    [Fact]
    public void Light_fills_every_letter_from_rest_to_one()
    {
        var spans = LyricTiming.Sweep("so far away", 0.0, 2.0);
        var into = new double[KaraokeLight.Letters(spans)];
        Assert.Equal(9, into.Length);

        KaraokeLight.Light(spans, -1.0, into);
        Assert.All(into, v => Assert.Equal(KaraokeLight.Rest, v, Near));

        // Every letter lit as the last word ends, and back at rest once the line has faded after it.
        KaraokeLight.Light(spans, spans[^1].To, into);
        Assert.All(into, v => Assert.Equal(1, v, Near));

        KaraokeLight.Light(spans, 10.0, into);
        Assert.All(into, v => Assert.Equal(KaraokeLight.Rest, v, Near));
    }
}

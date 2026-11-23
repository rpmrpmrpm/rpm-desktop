using RPM.Core.Lyrics;

namespace RPM.Core.Tests;

/// <summary>
/// The choice of sheet for an explicit record. LRCLIB often holds a clean sheet and an explicit sheet for one
/// song, and the record marked explicit must get the explicit one. The same cases are tested in the iPhone app
/// (RPMKit <c>ExplicitLyricsTests.swift</c>) and the Android apps (core <c>ExplicitLyricsTest.kt</c>).
/// </summary>
public class ExplicitLyricsTests
{
    private const string Masked = "[00:01.00]You think you can f*** with me\n[00:04.00]Get the **** out of my way";
    private const string Full = "[00:01.00]You think you can fuck with me\n[00:04.00]Get the fuck out of my way";
    private const string Dropped = "[00:01.00]You think you can mess with me\n[00:04.00]Get out of my way";
    private const string PlainA = "[00:01.00]Hello there\n[00:04.00]Nice to meet you";
    private const string PlainB = "[00:01.10]Hello there\n[00:04.10]Nice to meet you";

    private static LyricSheet Sheet(string lrc) => LyricSheet.Parse(lrc);

    [Fact]
    public void A_masked_word_costs_more_than_an_explicit_word_earns()
    {
        Assert.Equal(-2 * ExplicitLyrics.MaskPenalty, ExplicitLyrics.Score(Sheet(Masked)));
        Assert.Equal(2, ExplicitLyrics.Score(Sheet(Full)));
        Assert.Equal(0, ExplicitLyrics.Score(Sheet(Dropped)));
    }

    [Fact]
    public void Every_common_mask_is_seen()
    {
        Assert.Equal(-ExplicitLyrics.MaskPenalty, ExplicitLyrics.Score(Sheet("[00:01.00]What the sh*t")));
        Assert.Equal(-ExplicitLyrics.MaskPenalty, ExplicitLyrics.Score(Sheet("[00:01.00]What the f#ck")));
        Assert.Equal(-ExplicitLyrics.MaskPenalty, ExplicitLyrics.Score(Sheet("[00:01.00]What the ###")));
        Assert.Equal(-ExplicitLyrics.MaskPenalty, ExplicitLyrics.Score(Sheet("[00:01.00]What the [Censored]")));
    }

    [Fact]
    public void A_number_sign_before_a_digit_is_not_a_mask()
        => Assert.Equal(0, ExplicitLyrics.Score(Sheet("[00:01.00]Straight in at #1")));

    [Fact]
    public void Punctuation_and_capitals_do_not_hide_an_explicit_word()
        => Assert.Equal(2, ExplicitLyrics.Score(Sheet("[00:01.00]\"Fuckin',\" she said. SHIT!")));

    [Fact]
    public void The_full_explicit_sheet_beats_the_masked_clean_sheet()
    {
        // The exact lookup returned the clean sheet. The search has both.
        var picked = ExplicitLyrics.Pick(Sheet(Masked), [Sheet(Masked), Sheet(Full)]);

        Assert.Equal(Sheet(Full).Source, picked.Source);
    }

    [Fact]
    public void The_full_explicit_sheet_beats_a_clean_sheet_that_drops_the_words()
    {
        var picked = ExplicitLyrics.Pick(Sheet(Dropped), [Sheet(Full)]);

        Assert.Equal(Sheet(Full).Source, picked.Source);
    }

    [Fact]
    public void A_tie_keeps_the_exact_answer()
    {
        var picked = ExplicitLyrics.Pick(Sheet(PlainB), [Sheet(PlainA), Sheet(PlainB)]);

        Assert.Equal(Sheet(PlainB).Source, picked.Source);
    }

    [Fact]
    public void A_tie_without_an_exact_answer_keeps_lrclibs_order()
    {
        var picked = ExplicitLyrics.Pick(LyricSheet.Empty, [Sheet(PlainA), Sheet(PlainB)]);

        Assert.Equal(Sheet(PlainA).Source, picked.Source);
    }

    [Fact]
    public void No_candidates_is_no_sheet()
    {
        Assert.False(ExplicitLyrics.Pick(LyricSheet.Empty, []).IsSynced);
        Assert.False(ExplicitLyrics.Pick(LyricSheet.Empty, [Sheet("words without times")]).IsSynced);
    }

    // ── through the lookup ──

    private static string Record(string lrc, double duration)
        => System.Text.Json.JsonSerializer.Serialize(new { duration, syncedLyrics = lrc });

    /// <summary>A service that answers the exact lookup with the masked sheet and the search with the masked
    /// sheet, a full sheet for a different cut, and the full sheet for this cut.</summary>
    private static LrcLibLookup Service() => new((url, _) => Task.FromResult<string?>(
        url.Contains("/get?", StringComparison.Ordinal)
            ? Record(Masked, 200)
            : $"[{Record(Masked, 200)},{Record(Full.Replace("01.00", "09.00", StringComparison.Ordinal), 260)},{Record(Full, 201)}]"));

    [Fact]
    public async Task An_explicit_record_gets_the_uncensored_sheet_within_the_slack()
    {
        var sheet = await Service().FindAsync("A", "B", null, 200, isExplicit: true);

        Assert.Equal(Full, sheet.Source);
    }

    [Fact]
    public async Task A_record_not_marked_explicit_keeps_the_exact_answer()
    {
        var sheet = await Service().FindAsync("A", "B", null, 200);

        Assert.Equal(Masked, sheet.Source);
    }
}

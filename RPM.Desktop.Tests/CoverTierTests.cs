using RPM.Desktop.Services.Art;

namespace RPM.Desktop.Tests;

/// <summary>
/// Which of a record's two pictures may be written over the other, and when a read that came back empty
/// should be believed.
///
/// <para>Both walls fill two tiers from three workers, in an order that is not controlled. These rules
/// decide these questions in one place, so both walls answer them the same way. The tests check what the
/// listener sees: a cover that has sharpened stays sharp, and a record whose art exists is asked for
/// again.</para>
/// </summary>
public class CoverTierTests
{
    /// <summary>A record on screen may get its sharp picture first and still have no resident picture. The
    /// whole-collection pass then reads a 128 pixel picture for it. That small picture must not replace the
    /// 512 pixel picture the listener is looking at.</summary>
    [Fact]
    public void The_collection_pass_does_not_blur_a_tile_that_has_already_sharpened()
        => Assert.True(CoverTier.KeepsSharp(readEdge: 128, wantedEdge: 512, hasSharp: true));

    /// <summary>When the wall is zoomed out, the small picture is what the wall asks for. The big picture
    /// would then cost about a megabyte a tile, for detail nobody can see.</summary>
    [Fact]
    public void Zoomed_out_to_the_resident_size_the_big_picture_goes()
        => Assert.False(CoverTier.KeepsSharp(readEdge: 128, wantedEdge: 128, hasSharp: true));

    [Fact]
    public void A_read_at_the_wanted_size_or_larger_always_lands()
        => Assert.False(CoverTier.KeepsSharp(readEdge: 640, wantedEdge: 512, hasSharp: true));

    [Fact]
    public void There_is_nothing_to_keep_when_no_sharp_picture_was_ever_read()
        => Assert.False(CoverTier.KeepsSharp(readEdge: 128, wantedEdge: 512, hasSharp: false));

    // ── what the loader offers ──

    [Fact]
    public void A_card_wearing_the_wanted_size_is_not_offered_again()
        => Assert.False(CoverTier.WorthAsking(coverEdge: 384, missedEdge: 0, wantedEdge: 384, missing: false));

    [Fact]
    public void A_card_a_rung_off_is_offered()
        => Assert.True(CoverTier.WorthAsking(coverEdge: 128, missedEdge: 0, wantedEdge: 384, missing: false));

    /// <summary>A record with no art anywhere is never asked for again in this session. Otherwise a wall of
    /// such records would cost one disk read each on every pass.</summary>
    [Fact]
    public void A_record_with_no_art_is_never_offered()
        => Assert.False(CoverTier.WorthAsking(coverEdge: 0, missedEdge: 0, wantedEdge: 384, missing: true));

    /// <summary>A failed read is remembered together with the size it failed at. This stops the loader from
    /// retrying in a loop. It asks again only when the wall wants a different size.</summary>
    [Fact]
    public void A_failed_read_is_not_retried_at_the_same_size()
        => Assert.False(CoverTier.WorthAsking(coverEdge: 128, missedEdge: 384, wantedEdge: 384, missing: false));

    [Fact]
    public void And_is_asked_again_at_the_next_size()
        => Assert.True(CoverTier.WorthAsking(coverEdge: 128, missedEdge: 384, wantedEdge: 512, missing: false));

    // ── believing a read that came back empty ──

    /// <summary>A record that has never shown a picture is taken to have no art. Recording this once keeps
    /// the loader from asking for it again.</summary>
    [Fact]
    public void Nothing_ever_shown_means_the_record_has_no_art()
        => Assert.True(CoverTier.Latches(hasAnyPicture: false));

    /// <summary>A record that already shows a cover has art, so an empty answer means the read failed. The
    /// cause may be a file being written, or a computer on the network that did not answer. If the record
    /// were marked as having no art, it would keep its small picture for the rest of the session, however
    /// far the wall zoomed in.</summary>
    [Fact]
    public void A_record_that_has_shown_a_cover_is_never_written_off()
        => Assert.False(CoverTier.Latches(hasAnyPicture: true));
}

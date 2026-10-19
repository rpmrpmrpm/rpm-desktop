using RPM.Core.Ripping;

namespace RPM.Core.Tests;

/// <summary>The disc ID is the one part of ripping that must be bit-exact: a single wrong offset
/// and MusicBrainz returns nothing for a disc it knows. These vectors are not computed by us: they
/// are disc IDs and offsets that MusicBrainz itself published for real releases, so they test the
/// implementation against the real service and not against our own reading of it.
/// </summary>
public class MusicBrainzDiscIdTests
{
    /// <summary>Builds a TOC the way a drive reports one: LBA, lead-in excluded. MusicBrainz
    /// publishes offsets WITH the 150-frame lead-in, so we take those and subtract it.</summary>
    private static DiscToc TocFromMusicBrainzOffsets(int firstTrack, int[] offsets, int leadoutOffset,
                                                     int dataTracksAtEnd = 0)
    {
        var tracks = offsets
            .Select((offset, i) => new DiscTrack(
                Number: firstTrack + i,
                Lba: offset - MusicBrainzDiscId.LeadIn,
                IsData: i >= offsets.Length - dataTracksAtEnd))
            .ToList();

        return new DiscToc(firstTrack, tracks[^1].Number, tracks,
            LeadoutLba: leadoutOffset - MusicBrainzDiscId.LeadIn);
    }

    // Release "Bat Out of Hell"-era 12-track CD; disc 2y0jFMwYQP7gb9iSUKagmoT6DIw-.
    private static readonly int[] TwelveTrackOffsets =
        [150, 22767, 41887, 58317, 72102, 91375, 104652, 115380, 132165, 143932, 159870, 174597];

    // Release "Ettella Diamant" (SK, 2004); disc 49HHV7Eb8UKF3aQiNmu1GR8vKTY-.
    private static readonly int[] SixTrackOffsets = [150, 15363, 32314, 46592, 63414, 80489];

    [Fact]
    public void Computes_the_disc_id_MusicBrainz_published_for_a_twelve_track_cd()
    {
        var toc = TocFromMusicBrainzOffsets(1, TwelveTrackOffsets, leadoutOffset: 267225);

        Assert.Equal("2y0jFMwYQP7gb9iSUKagmoT6DIw-", MusicBrainzDiscId.Compute(toc));
    }

    [Fact]
    public void Computes_the_disc_id_MusicBrainz_published_for_a_six_track_cd()
    {
        var toc = TocFromMusicBrainzOffsets(1, SixTrackOffsets, leadoutOffset: 95462);

        Assert.Equal("49HHV7Eb8UKF3aQiNmu1GR8vKTY-", MusicBrainzDiscId.Compute(toc));
    }

    /// <summary>The TOC macOS reported for a real CD in a real drive ("The Very Best of Oleta
    /// Adams", 16 tracks), copied exactly from its <c>.TOC.plist</c> "Start Block" values, and the
    /// disc ID MusicBrainz returned for it. cddafs block addresses already include the 150-frame
    /// lead-in, so they are offsets, not LBAs; reading them as LBAs shifts every offset and the disc
    /// is not found.</summary>
    [Fact]
    public void Computes_the_disc_id_for_a_real_cd_read_off_a_real_drive()
    {
        int[] startBlocks =
        [
            150, 19852, 40452, 60105, 78430, 105115, 120345, 141802,
            165132, 182617, 206312, 225865, 244770, 261517, 282817, 312190,
        ];
        const int leadoutBlock = 337065;

        var tracks = startBlocks
            .Select((block, i) => new DiscTrack(i + 1, MacDiscReader.StartBlockToLba(block), IsData: false))
            .ToList();
        var toc = new DiscToc(1, 16, tracks, MacDiscReader.StartBlockToLba(leadoutBlock));

        Assert.Equal("BGMWt_g4ypmfmlZ9HH4MIVN3Jmw-", MusicBrainzDiscId.Compute(toc));

        // The offsets we hash must convert back to the block addresses the drive gave us.
        var offsets = MusicBrainzDiscId.BuildOffsets(toc);
        Assert.Equal(leadoutBlock, offsets[0]);
        Assert.Equal(startBlocks, offsets[1..17]);
    }

    [Fact]
    public void Offset_table_puts_the_leadout_first_and_leaves_absent_tracks_zero()
    {
        var toc = TocFromMusicBrainzOffsets(1, SixTrackOffsets, leadoutOffset: 95462);

        var offsets = MusicBrainzDiscId.BuildOffsets(toc);

        Assert.Equal(100, offsets.Length);
        Assert.Equal(95462, offsets[0]);
        Assert.Equal(SixTrackOffsets, offsets[1..7]);
        Assert.All(offsets[7..], slot => Assert.Equal(0, slot));
    }

    [Fact]
    public void Trailing_data_track_stands_in_for_the_leadout_backed_off_by_the_xa_interval()
    {
        // An enhanced/CD-Extra disc: six audio tracks then a data track. libdiscid ignores the
        // physical lead-out and derives it from the data track's start instead.
        var withData = SixTrackOffsets.Append(90000).ToArray();
        var toc = TocFromMusicBrainzOffsets(1, withData, leadoutOffset: 120000, dataTracksAtEnd: 1);

        var offsets = MusicBrainzDiscId.BuildOffsets(toc);

        Assert.Equal(90000 - MusicBrainzDiscId.XaInterval, offsets[0]);
        Assert.Equal(0, offsets[7]);                       // the data track gets no slot of its own
        Assert.Equal(6, MusicBrainzDiscId.LastAudioTrack(toc));

        // ...and the audio tracks alone decide the ID, exactly as on the plain 6-track disc.
        // Only the substituted lead-out differs, so it must NOT give the same ID as the plain disc.
        Assert.NotEqual("49HHV7Eb8UKF3aQiNmu1GR8vKTY-", MusicBrainzDiscId.Compute(toc));
    }

    [Fact]
    public void A_track_reporting_address_zero_clamps_to_the_lead_in()
    {
        // Some copy-protected discs report a zero start address; libdiscid sets those to 150
        // instead of writing a 0 offset, which would hash to a completely different disc.
        var tracks = new List<DiscTrack> { new(1, 0, false), new(2, 20000, false) };
        var toc = new DiscToc(1, 2, tracks, LeadoutLba: 40000);

        var offsets = MusicBrainzDiscId.BuildOffsets(toc);

        Assert.Equal(MusicBrainzDiscId.LeadIn, offsets[1]);
        Assert.Equal(20000 + MusicBrainzDiscId.LeadIn, offsets[2]);
    }

    [Fact]
    public void Toc_string_is_the_fuzzy_lookup_shape_MusicBrainz_expects()
    {
        var toc = TocFromMusicBrainzOffsets(1, SixTrackOffsets, leadoutOffset: 95462);

        // first + last + leadout + one offset per track
        Assert.Equal("1+6+95462+150+15363+32314+46592+63414+80489", MusicBrainzDiscId.TocString(toc));
    }

    [Fact]
    public void Track_durations_run_to_the_next_track_and_the_last_one_to_the_leadout()
    {
        var toc = TocFromMusicBrainzOffsets(1, SixTrackOffsets, leadoutOffset: 95462);

        var first = toc.Tracks[0];
        var last = toc.Tracks[^1];

        Assert.Equal(15363 - 150, toc.SectorCountOf(first));
        Assert.Equal(95462 - 80489, toc.SectorCountOf(last));
        Assert.Equal((15363 - 150) / 75.0, toc.DurationOf(first).TotalSeconds, precision: 6);
    }
}

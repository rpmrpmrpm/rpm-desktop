using RPM.Core.Ripping;

namespace RPM.Core.Tests;

/// <summary>Track sizing on an enhanced (CD-Extra) disc, where a data track in a second session
/// follows the audio. The vectors are the real TOC of an "Opendisc" CD-Extra: 12 audio tracks, a
/// data track at LBA 207647, and the audio session's lead-out at 196247. Ending the last audio track
/// at the data track's start (207647) instead of the audio lead-out (196247) makes the ripper read
/// the ~4650 unreadable sectors of the session gap and abandon the track, because the drive refuses
/// to read inside that gap.</summary>
public class DiscTocTests
{
    private static readonly DiscTrack Track11 = new(11, 168145, IsData: false);
    private static readonly DiscTrack Track12 = new(12, 183003, IsData: false);   // last audio track
    private static readonly DiscTrack DataTrack = new(13, 207647, IsData: true);  // the "Opendisc" session
    private const int AudioLeadout = 196247;   // session 1's lead-out (POINT 0xA2)
    private const int FinalLeadout = 237547;   // the whole disc's lead-out (session 2)

    private static DiscToc CdExtra(int? audioLeadout) =>
        new(FirstTrack: 1, LastTrack: 13, new[] { Track11, Track12, DataTrack }, FinalLeadout, audioLeadout);

    [Fact]
    public void Last_audio_track_ends_at_the_audio_session_leadout_not_the_data_track()
    {
        var toc = CdExtra(AudioLeadout);
        Assert.Equal(AudioLeadout, toc.EndLbaOf(Track12));
        Assert.Equal(AudioLeadout - Track12.Lba, toc.SectorCountOf(Track12));   // 13244 sectors, ~2:56
    }

    [Fact]
    public void Without_the_audio_leadout_the_last_audio_track_falls_back_to_the_data_track()
    {
        // The fallback when the multisession TOC cannot be read.
        var toc = CdExtra(audioLeadout: null);
        Assert.Equal(DataTrack.Lba, toc.EndLbaOf(Track12));
    }

    [Fact]
    public void A_middle_audio_track_still_ends_at_the_next_track()
    {
        var toc = CdExtra(AudioLeadout);
        Assert.Equal(Track12.Lba, toc.EndLbaOf(Track11));
    }

    [Fact]
    public void An_ordinary_disc_ends_the_last_track_at_the_leadout()
    {
        var t1 = new DiscTrack(1, 0, IsData: false);
        var t2 = new DiscTrack(2, 20870, IsData: false);
        var toc = new DiscToc(1, 2, new[] { t1, t2 }, LeadoutLba: 50000);
        Assert.Equal(50000, toc.EndLbaOf(t2));
    }
}

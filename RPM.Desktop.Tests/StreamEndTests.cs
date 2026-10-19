using RPM.Desktop.Services.Audio;
using static RPM.Desktop.Services.Audio.PlaybackService;

namespace RPM.Desktop.Tests;

/// <summary>
/// How a streamed track running out is read: played out, or broken and to be opened again.
///
/// <para>This covers a track from a borrowed shelf whose stream ends cleanly with no audio. ffmpeg exits
/// with zero and nothing on stderr when the server answers and sends no body. That must not count as the
/// track finishing, or the queue would move on in silence.</para>
/// </summary>
public class StreamEndTests
{
    [Fact]
    public void A_clean_end_with_nothing_heard_is_a_break()
    {
        Assert.Equal(TrackEnd.Broke, ReadEnd(failed: false, durationSeconds: 240, reachedSeconds: 0, heardSeconds: 0));
    }

    [Fact]
    public void A_clean_end_with_nothing_heard_after_a_seek_is_a_break()
    {
        Assert.Equal(TrackEnd.Broke, ReadEnd(failed: false, durationSeconds: 240, reachedSeconds: 90, heardSeconds: 0.2));
    }

    [Fact]
    public void A_clean_end_partway_through_after_audio_is_a_finish()
    {
        // A tag whose duration is far too long: the music was heard and then ended. Opening it again
        // would replay nothing and stop the queue on a track that played.
        Assert.Equal(TrackEnd.PlayedOut, ReadEnd(failed: false, durationSeconds: 400, reachedSeconds: 200, heardSeconds: 200));
    }

    [Fact]
    public void A_failure_short_of_the_end_is_a_break()
    {
        Assert.Equal(TrackEnd.Broke, ReadEnd(failed: true, durationSeconds: 240, reachedSeconds: 80, heardSeconds: 80));
    }

    [Fact]
    public void A_failure_at_the_end_is_a_finish()
    {
        Assert.Equal(TrackEnd.PlayedOut, ReadEnd(failed: true, durationSeconds: 240, reachedSeconds: 237, heardSeconds: 237));
    }

    [Fact]
    public void A_track_shorter_than_the_slack_that_ends_cleanly_is_a_finish()
    {
        Assert.Equal(TrackEnd.PlayedOut, ReadEnd(failed: false, durationSeconds: 0.5, reachedSeconds: 0.4, heardSeconds: 0.4));
    }
}

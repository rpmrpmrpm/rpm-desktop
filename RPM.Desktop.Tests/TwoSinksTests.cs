using System.Diagnostics;
using RPM.Core.Audio;
using RPM.Desktop.Services.Audio;

namespace RPM.Desktop.Tests;

/// <summary>
/// Two sinks open at once, which is what the app does every time it is started: the opening sound is
/// still playing when a record is put on, and it is told to get out of the way.
///
/// <para>This test needs the real sound device. The fault lives in OpenAL. There the current context is
/// process-wide, and every source is numbered from one inside its own context. A sink that makes its
/// calls without claiming its own context first is talking about the OTHER sink's source. The opening
/// sound then stops, rewinds, empties and finally deletes the player's source. The player then feeds a
/// queue that the library never received a block of. The listener hears a record that goes silent a
/// second after it starts, and pauses itself ten seconds later.</para>
///
/// <para>Silent and short: the blocks are zeroes at zero volume, and the whole thing is over in about
/// a second and a half. A machine with no playback device at all cannot be asked the question, and
/// says so rather than failing.</para>
/// </summary>
[Collection(TimingCollection.Name)]
public class TwoSinksTests
{
    private const int Rate = 44100;
    private const int Channels = 2;
    private const int Frames = Rate / 20;        // 50 ms, the block the engine feeds

    private static SinkTrack Track(string title) => new("", title, null, null, 60, 0);

    [Fact]
    public async Task The_opening_sound_getting_out_of_the_way_leaves_the_player_playing()
    {
        var opening = new OpenAlSink();
        if (!opening.IsAvailable)
        {
            opening.Dispose();
            return;             // no sound device on this machine; there is nothing to hold to account
        }

        // The mechanism, not just the outcome. Without a context per thread, the two sinks share one slot
        // and can only take turns at it. This class of fault is one scheduling accident away from coming
        // back.
        Assert.True(OpenAlSink.ContextsArePerThread,
                    "the sound library has no per-thread contexts here: two sinks share one slot");

        var block = new float[Frames * Channels];

        // The opening sound, playing.
        opening.SetVolume(0);
        opening.Begin(Track("rpm"), Rate, Channels);
        for (int i = 0; i < 4; i++) opening.Enqueue(block, Frames);

        // A record put on over the top of it. Begin claims the process-wide context, which is the
        // moment the opening sound's every later call starts landing on this sink's source.
        using var player = new OpenAlSink();
        player.SetVolume(0);
        player.Begin(Track("a record"), Rate, Channels);

        // Fed from a thread of its own, because Enqueue is the pacer: it parks until the device gives
        // a buffer back, which is exactly what stops happening when this fault bites.
        var feeding = true;
        var feeder = Task.Run(() => { while (Volatile.Read(ref feeding)) player.Enqueue(block, Frames); });

        try
        {
            Assert.True(Reaches(() => player.FramesPlayed > 0),
                        "the player never started: the device played nothing at all");

            // What App.xaml.cs does the instant a record starts: stop the flourish and let it go.
            opening.Stop();
            opening.Dispose();

            long before = player.FramesPlayed;
            Assert.True(Reaches(() => player.FramesPlayed > before + Frames),
                        $"the player stopped when the opening sound did: {before} frames played and no more");
            Assert.Equal(0, player.Underruns);
        }
        finally
        {
            Volatile.Write(ref feeding, false);
            player.Stop();
            await Settled(feeder);
        }
    }

    /// <summary>
    /// The record put on while the opening sound is still going, and held to the clock.
    ///
    /// <para>A sink is the pump's only pacer. <see cref="OpenAlSink.Enqueue"/> parks until the device gives
    /// a buffer back, and the device gives them back at exactly the speed it is playing. One second of
    /// wall clock is twenty 50 ms blocks plus whatever the queue holds. A sink that takes hundreds is not
    /// playing them. It is swallowing them, and the record is gone by the time the flourish ends.</para>
    /// </summary>
    [Fact]
    public async Task A_record_started_over_the_opening_sound_is_paced_by_the_clock()
    {
        var opening = new OpenAlSink();
        if (!opening.IsAvailable)
        {
            opening.Dispose();
            return;             // no sound device on this machine
        }

        var block = new float[Frames * Channels];

        // The flourish, playing out of its own device and fed from its own thread, as AppSound does.
        opening.SetVolume(0);
        opening.Begin(Track("rpm"), Rate, Channels);
        var flourishing = true;
        var flourish = Task.Run(() =>
        {
            while (Volatile.Read(ref flourishing) && opening.Enqueue(block, Frames)) { }
        });

        try
        {
            Assert.True(Reaches(() => opening.FramesPlayed > 0), "the opening sound never started");

            // The record, over the top of it.
            using var player = new OpenAlSink();
            Assert.True(player.IsAvailable,
                        "the player could not open a device while the opening sound held one");

            player.SetVolume(0);
            player.Begin(Track("a record"), Rate, Channels);

            int taken = 0;
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 1000)
            {
                if (!player.Enqueue(block, Frames)) break;
                taken++;
            }

            // Twenty blocks of audio in a second, plus the ten the queue holds, plus a little slack for
            // a device that buffers a touch more than it says.
            Assert.True(taken <= 45,
                        $"the record was swallowed rather than played: {taken} blocks " +
                        $"({taken * 0.05:F1}s of audio) taken in {clock.ElapsedMilliseconds}ms");
            Assert.True(taken >= 15,
                        $"the record barely moved: {taken} blocks in {clock.ElapsedMilliseconds}ms");

            player.Stop();
        }
        finally
        {
            Volatile.Write(ref flourishing, false);
            opening.Stop();
            await Settled(flourish);
            opening.Dispose();
        }
    }

    /// <summary>Gives a feeding thread two seconds to notice it has been stopped. Raced against a timer
    /// rather than blocked on, and never thrown from: this runs in a finally, where a throw would stand
    /// in for whatever the test itself found, and a blocked pool thread is one fewer for the device's
    /// own callbacks.</summary>
    private static Task Settled(Task feeding) => Task.WhenAny(feeding, Task.Delay(TimeSpan.FromSeconds(2)));

    /// <summary>Waits for a fact to become true, up to a second and a half: long enough for a healthy
    /// device to have played several blocks, short enough that a dead one does not hold the
    /// suite.</summary>
    /// <summary>Waits for something the sound device does. The faults here are stalls that never end (a
    /// source that never gets a buffer back), so the limit only has to be longer than a slow start of a
    /// real device and the feeder thread. Five seconds is that, on a busy machine; a stall is still a
    /// failure.</summary>
    private static bool Reaches(Func<bool> fact)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 5000)
        {
            if (fact()) return true;
            Thread.Sleep(10);
        }
        return false;
    }
}

using System.Collections.Concurrent;
using RPM.Core.Audio;

namespace RPM.Core.Tests;

/// <summary>
/// The volume conversation with a cast speaker: who is believed when the two ends disagree, and what
/// actually goes on the wire.
/// </summary>
// The sender waits with Task.Delay and needs a pool thread to carry on. Beside the other tests, which block
// pool threads of their own, that thread can be seconds coming, and the count of sends depends on the
// sender's pace against the interval. So these run with nothing beside them.
[Collection(TimingCollection.Name)]
public class CastVolumeTests
{
    /// <summary>A speaker that records what it was told, and can be waited on.</summary>
    private sealed class Speaker
    {
        private readonly ConcurrentQueue<double> _sent = new();

        public Func<double, Task> Send => level =>
        {
            _sent.Enqueue(level);
            return Task.CompletedTask;
        };

        public IReadOnlyList<double> Sent => [.. _sent];

        public bool Heard(double level, TimeSpan within)
        {
            var deadline = DateTime.UtcNow + within;
            while (DateTime.UtcNow < deadline)
            {
                if (_sent.Any(l => Math.Abs(l - level) < 0.005)) return true;
                Thread.Sleep(10);
            }

            return false;
        }
    }

    private static CastVolume Quick(Speaker speaker)
        => new(speaker.Send, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(200));

    /// <summary>
    /// The push a sink gets the moment it is fitted does not reach the speaker.
    ///
    /// <para>This is the case the type exists for: the engine gives its own level to every new sink,
    /// and casting that number sets a speaker at 20% to whatever the player happened to be at.</para>
    /// </summary>
    [Fact]
    public void A_Level_Set_Before_There_Is_A_Stream_Is_Only_Remembered()
    {
        var speaker = new Speaker();
        var volume = Quick(speaker);

        volume.Set(100, live: false);

        Assert.Equal(100, volume.Level);
        Thread.Sleep(60);
        Assert.Empty(speaker.Sent);
    }

    [Fact]
    public void A_Level_Set_On_A_Live_Stream_Reaches_The_Speaker()
    {
        var speaker = new Speaker();
        var volume = Quick(speaker);

        volume.Set(40, live: true);

        Assert.True(speaker.Heard(0.4, TimeSpan.FromSeconds(5)), "the speaker was never turned down");
    }

    /// <summary>The speaker's own level is taken as the player's.</summary>
    [Fact]
    public void The_Rooms_Level_Is_Adopted_When_We_Have_Not_Asked_For_Anything()
    {
        var volume = Quick(new Speaker());

        volume.Set(100, live: false);       // the player is loud; the speaker is not

        Assert.Equal(20, volume.Adopt(0.2));
        Assert.Equal(20, volume.Level);
    }

    [Fact]
    public void A_Speaker_That_Has_Not_Said_Changes_Nothing()
        => Assert.Null(Quick(new Speaker()).Adopt(null));

    /// <summary>
    /// A status that arrives while the level is moving does not drag it backwards. The speaker reports
    /// where it was when it answered, so during a drag its statuses are stale by definition.
    /// </summary>
    [Fact]
    public void A_Stale_Report_Is_Not_Believed_Over_Our_Own_Change()
    {
        var volume = Quick(new Speaker());

        volume.Set(40, live: true);
        volume.Set(60, live: true);
        volume.Set(80, live: true);

        Assert.Null(volume.Adopt(0.4));     // the speaker answering the first of them, late
        Assert.Equal(80, volume.Level);
    }

    /// <summary>Once we have sent nothing for a while, the speaker's own buttons win, because by then
    /// they are what turned it.</summary>
    [Fact]
    public void Once_We_Have_Been_Quiet_The_Speakers_Own_Buttons_Win()
    {
        var volume = Quick(new Speaker());

        volume.Set(40, live: true);
        Thread.Sleep(250);                  // longer than this conversation's echo window

        Assert.Equal(65, volume.Adopt(0.65));
    }

    /// <summary>A point either way is the speaker's own quantisation answering back, not somebody
    /// turning it. Following it would nudge the level off the number the listener just chose.</summary>
    [Theory]
    [InlineData(0.39)]
    [InlineData(0.40)]
    [InlineData(0.41)]
    public void A_Point_Either_Way_Is_Not_A_Change(double reported)
    {
        var volume = Quick(new Speaker());

        volume.Set(40, live: false);

        Assert.Null(volume.Adopt(reported));
        Assert.Equal(40, volume.Level);
    }

    /// <summary>
    /// A drag is one send, not fifty. The speaker answers every change with a status, and answers to
    /// changes we have already moved past arrive out of order with the drag.
    /// </summary>
    [Fact]
    public void A_Flurry_Of_Changes_Is_Coalesced_To_The_Last_Of_Them()
    {
        var speaker = new Speaker();
        var volume = new CastVolume(
            speaker.Send, TimeSpan.FromMilliseconds(60), CastVolume.DefaultEcho);

        for (var v = 40; v <= 80; v++) volume.Set(v, live: true);

        Assert.True(speaker.Heard(0.8, TimeSpan.FromSeconds(5)), "the last of them never landed");

        // Forty-one changes; nothing like forty-one sends.
        Assert.InRange(speaker.Sent.Count, 1, 5);
    }

    [Fact]
    public void Nothing_Goes_Out_After_The_Cast_Is_Closed()
    {
        var speaker = new Speaker();
        var volume = Quick(speaker);

        volume.Close();
        volume.Set(30, live: true);

        Thread.Sleep(60);
        Assert.Empty(speaker.Sent);
    }

    [Theory]
    [InlineData(-20, 0)]
    [InlineData(160, 100)]
    public void The_Level_Is_Clamped(int asked, int expected)
    {
        var volume = Quick(new Speaker());

        volume.Set(asked, live: false);

        Assert.Equal(expected, volume.Level);
    }
}

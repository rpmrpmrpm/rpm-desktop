using RPM.Core.Analysis;
using RPM.Core.Audio;
using RPM.Core.Mixing;

namespace RPM.Core.Tests;

/// <summary>
/// The bass swap as it is heard in the mix, rather than as the plan describes it.
///
/// <para>Everything in <see cref="BassSwapTests"/> reads the arc: what the plan SAYS each deck's
/// level and bass should be at each phrase line. That is worth pinning and it is not the same
/// question as this one. Between the arc and the speakers there is a booth reading it once a block,
/// two decks each running a filter, and a fader, and the move works only if that chain does what the
/// arc says. A plan with a perfect bass swap in it, given to a deck whose EQ is not connected,
/// produces two records playing at once and passes every test in that file.</para>
///
/// <para>So nothing here reads a plan. Each record is given a bass note of its own (the one leaving
/// at 60 Hz, the one arriving at 90 Hz, over a carrier they share), and what is measured is how much
/// of each note is in the mix, block by block, straight out of <see cref="Dj.Read"/>. Goertzel rather
/// than a full transform: two known frequencies, and it is exact at both.</para>
/// </summary>
public class BassSwapHeardTests
{
    private const int Rate = 44100, Channels = 2, Block = 512;
    private const int Bpm = 120;

    /// <summary>
    /// How many frames one reading is taken over. Not the block the booth is driven with: at 512
    /// samples a bin is 86 Hz wide, so two bass notes eighty Hz apart land in the SAME bin and the
    /// measurement cannot tell the two records apart at all, which looks exactly like a bass swap
    /// that did not happen. 8192 frames puts a bin at 5.4 Hz and the two notes fifteen bins apart.
    /// </summary>
    private const int Window = 8192;

    /// <summary>
    /// The bass note each record carries, and the tone both carry so there is something for the faders
    /// to cross that the EQ is not touching.
    ///
    /// <para>Each is an exact multiple of <c>Rate / Window</c>, so it sits on the centre of a bin and
    /// leaks into none of the others. A tone between two bins spreads across the whole spectrum, and
    /// the spread of the loud one would be read as the quiet one still being there.</para>
    /// </summary>
    private const double Bin = (double)Rate / Window;                  // 5.383 Hz
    private const double OutBassHz = Bin * 11;                         // 59.2 Hz
    private const double InBassHz = Bin * 17;                          // 91.5 Hz
    private const double SharedHz = Bin * 372;                         // 2002 Hz

    // ── the rig ──────────────────────────────────────────────────────────────

    /// <summary>A record that is a bass note plus a shared carrier. Which bass note depends on which
    /// file was asked for, which is how the two decks can be told apart in one mixed buffer.</summary>
    private sealed class Bell(double bassHz, double startSeconds, double tempoRatio) : IPcmSource
    {
        private long _at = (long)(startSeconds * Rate);

        public string Label => "bell";

        public int Read(float[] buffer, int frames)
        {
            for (int f = 0; f < frames; f++)
            {
                double t = (double)(_at + f) / Rate * tempoRatio;
                var v = (float)(0.4 * Math.Sin(2 * Math.PI * bassHz * t)
                              + 0.3 * Math.Sin(2 * Math.PI * SharedHz * t));

                for (int c = 0; c < Channels; c++)
                    buffer[f * Channels + c] = v;
            }

            _at += frames;
            return frames;
        }

        public void Dispose() { }
    }

    private static MixTrack Track(string name, double duration, double outro, double intro = 0)
    {
        var beats = new List<double>();
        for (double at = 0; at < duration; at += 60.0 / Bpm)
            beats.Add(at);

        string path = $"/library/{name}.flac";
        return new MixTrack
        {
            Source = path,
            Path = path,
            Title = name,
            TaggedDuration = duration,
            Analysis = new TrackAnalysis
            {
                FilePath = path,
                DurationSeconds = duration,
                Bpm = Bpm,
                BpmConfidence = 0.9,
                BeatTimes = beats,
                FirstDownbeat = 0,
                BeatsPerBar = 4,
                Key = new MusicalKey(9, true),
                KeyConfidence = 0.8,
                EnergyCurve = [0.3f, 0.3f],
                IntroEndSeconds = intro,
                OutroStartSeconds = outro,
                Phrases = new PhraseGrid(32, 0, 1),
            },
        };
    }

    /// <summary>How much of a tone at <paramref name="hz"/> is in one window, as an amplitude.
    /// Goertzel: one bin of a transform, which is all that is wanted here.</summary>
    private static double Level(float[] block, int frames, double hz)
    {
        double w = 2 * Math.PI * hz / Rate;
        double cos = Math.Cos(w), coeff = 2 * cos;
        double s1 = 0, s2 = 0;

        for (int f = 0; f < frames; f++)
        {
            double s = block[f * Channels] + coeff * s1 - s2;
            s2 = s1;
            s1 = s;
        }

        double real = s1 - s2 * cos;
        double imaginary = s2 * Math.Sin(w);
        return 2 * Math.Sqrt(real * real + imaginary * imaginary) / frames;
    }

    /// <summary>One reading of the mix: where the changeover is, and how much of each record's bass
    /// note is in the mix.</summary>
    private readonly record struct Heard(double At, double OutBass, double InBass, double Shared);

    /// <summary>Runs a bass swap and reports every block of it.</summary>
    private static List<Heard> Run()
    {
        var tracks = new[]
        {
            Track("out", duration: 400, outro: 300),
            Track("in", duration: 400, outro: 380),
        };

        using var dj = new Dj(
            new DjCrate(tracks), Rate, Channels,
            options => new Bell(
                options.Source.Contains("out", StringComparison.Ordinal) ? OutBassHz : InBassHz,
                options.StartSeconds, options.TempoRatio),
            cueRunner: work => { work(); return Task.CompletedTask; },
            chance: () => 1)
        {
            Overrides = new MixOverrides { Style = TransitionStyle.BassSwap },
        };

        var block = new float[Block * Channels];
        var window = new float[Window * Channels];
        var heard = new List<Heard>();

        int filled = 0;
        double startedAt = 0;
        bool swapped = false;

        for (int i = 0; i < 200_000; i++)
        {
            if (dj.Read(block, Block) <= 0)
                break;

            double at = dj.ChangeoverProgress;
            if (at <= 0)
            {
                if (heard.Count > 0)
                    break;                          // the changeover is over
                filled = 0;
                continue;
            }

            if (!swapped)
            {
                swapped = true;
                // The PENDING plan, not the current one. During a changeover _current is still the
                // plan that brought the record PLAYING in (it only becomes the new one when the fade
                // ends), so reading it here reports the last changeover's style for the whole of this
                // one, which at the start of a set is the opening.
                Assert.Equal(TransitionStyle.BassSwap, dj.PendingPlan?.Style ?? dj.CurrentPlan?.Style);
            }

            if (filled == 0)
                startedAt = at;

            Array.Copy(block, 0, window, filled * Channels, Block * Channels);
            filled += Block;

            if (filled < Window)
                continue;

            heard.Add(new Heard(
                (startedAt + at) / 2,
                Level(window, Window, OutBassHz),
                Level(window, Window, InBassHz),
                Level(window, Window, SharedHz)));

            filled = 0;
        }

        Assert.True(heard.Count > 40, $"the set never ran a changeover worth measuring ({heard.Count} windows)");
        return heard;
    }

    /// <summary>Everything read between two points of the changeover.</summary>
    private static List<Heard> Between(List<Heard> heard, double from, double to) =>
        [.. heard.Where(h => h.At >= from && h.At <= to)];

    private static double Loudest(List<Heard> window, Func<Heard, double> of) => window.Max(of);
    private static double Quietest(List<Heard> window, Func<Heard, double> of) => window.Min(of);

    // ── the move ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The record coming in is in the mix with no bottom end at all.
    ///
    /// <para>This is the core of the move. Two records both carrying a bassline is the muddy sound a
    /// bass swap exists to avoid, so for the first phrase the new one has to be audible (its carrier at
    /// full) and its bass not there.</para>
    /// </summary>
    [Fact]
    public void The_record_coming_in_arrives_with_no_bass_at_all()
    {
        var heard = Run();

        // Its own bass at the very top of the changeover, against what it is by the end of it.
        var arriving = Between(heard, 0, 0.15);
        var landed = Between(heard, 0.85, 1);

        double atFirst = Loudest(arriving, h => h.InBass);
        double atLast = Loudest(landed, h => h.InBass);

        Assert.True(
            atFirst <= atLast / 30,
            $"the record coming in arrived with {20 * Math.Log10(atFirst / Math.Max(atLast, 1e-9)):F1} dB " +
            $"of the bass it ends the changeover with, which is a bassline and not a bass swap");

        // And it is in the mix while its bass is not: the shared tone is there from the start, so
        // what was taken away was the bottom end and not the record.
        Assert.True(
            Loudest(arriving, h => h.Shared) > 0.1,
            "the record coming in was not audible at all over the first phrase");
    }

    /// <summary>
    /// The bass is turned up on the record coming in, rather than switched on. It rises across the
    /// middle of the changeover, a fraction of a dB per block, so the handover cannot be heard as a
    /// single moment.
    /// </summary>
    [Fact]
    public void The_bass_is_ridden_up_on_the_record_coming_in()
    {
        var heard = Run();

        double quietest = Quietest(heard, h => h.InBass);
        double loudest = Loudest(heard, h => h.InBass);

        // It really does travel the whole way.
        Assert.True(loudest > quietest * 30, "the incoming bass never came up");

        // And never in one go. The largest jump between two consecutive readings is a small share of
        // the whole climb; a switch would put the entire climb in one of them.
        double biggest = 0;
        double atBiggest = 0;
        for (int i = 1; i < heard.Count; i++)
        {
            double jump = Math.Abs(heard[i].InBass - heard[i - 1].InBass);
            if (jump > biggest)
            {
                biggest = jump;
                atBiggest = heard[i].At;
            }
        }

        Assert.True(
            biggest < (loudest - quietest) * 0.35,
            $"the incoming bass moved {biggest / (loudest - quietest):P0} of its whole travel in one " +
            $"block, {atBiggest:P0} through the changeover: that is a switch, not a ride");
    }

    /// <summary>
    /// And it is turned down on the record playing, over the same stretch. The bottom end belongs to
    /// one record at a time. The point is not that the new one gets bass, it is that the two never
    /// have it together.
    /// </summary>
    [Fact]
    public void The_bass_is_ridden_down_on_the_record_playing()
    {
        var heard = Run();

        double atFirst = Loudest(Between(heard, 0, 0.15), h => h.OutBass);
        double atLast = Loudest(Between(heard, 0.85, 1), h => h.OutBass);

        Assert.True(atFirst > 0.05, "the record playing had no bass to begin with");
        Assert.True(
            atLast <= atFirst / 30,
            $"the record playing kept {20 * Math.Log10(atLast / Math.Max(atFirst, 1e-9)):F1} dB of its " +
            $"bass to the end of the changeover");
    }

    /// <summary>
    /// The two never share the bottom end. Measured as the sum of the two bass notes at every block
    /// of the changeover: it may dip while the handover is happening, and it must never rise above
    /// what one record on its own was putting out.
    ///
    /// <para>This is the main test of the move. Both faders up and both basslines in is exactly the
    /// muddy sound the swap avoids, and it is the one failure that a listener hears immediately and no
    /// arc, level or bar line shows.</para>
    /// </summary>
    [Fact]
    public void The_two_records_never_share_the_bottom_end()
    {
        var heard = Run();

        // One record's worth, taken from the top of the changeover where the old one has it alone.
        double alone = Loudest(Between(heard, 0, 0.1), h => h.OutBass);

        double worst = 0, worstAt = 0;
        foreach (var h in heard)
        {
            double together = h.OutBass + h.InBass;
            if (together > worst)
            {
                worst = together;
                worstAt = h.At;
            }
        }

        Assert.True(
            worst <= alone * 1.15,
            $"the two records had {worst / alone:P0} of one record's bass in the room between them, " +
            $"{worstAt:P0} through the changeover");
    }

    /// <summary>
    /// And the handover happens in the middle of the changeover rather than at either end: the new
    /// record has a phrase with no bass, the swap runs, and the old record is faded away above it.
    /// </summary>
    [Fact]
    public void The_handover_happens_in_the_middle_of_the_changeover()
    {
        var heard = Run();

        // Where the two bass notes are level with each other, which is the middle of the swap.
        var crossing = heard
            .Select((h, i) => (At: h.At, Gap: h.InBass - h.OutBass, Index: i))
            .FirstOrDefault(x => x.Gap >= 0);

        Assert.True(crossing.At > 0, "the two records' bass never crossed at all");
        Assert.InRange(crossing.At, 0.2, 0.8);
    }
}

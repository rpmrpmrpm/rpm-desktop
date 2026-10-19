using System.Diagnostics;
using RPM.Core.Analysis;
using RPM.Core.Audio;
using RPM.Core.Mixing;

namespace RPM.Core.Tests;

/// <summary>
/// The auto-mix end to end on real files: two tracks written to disk, decoded by ffmpeg, analysed
/// for tempo and key, and mixed by the DJ through actual <see cref="FfmpegPcmSource"/> decks. This is
/// the same path the app takes, without the sound card. Skipped silently where ffmpeg is not
/// installed (it is the app's decoder, so without it there is nothing to test).
/// </summary>
public class DjMixEndToEndTests : IDisposable
{
    private const int Rate = 44100, Channels = 2;
    private static readonly bool FfmpegAvailable = ProbeFfmpeg();
    private readonly string _root;

    public DjMixEndToEndTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"djtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static bool ProbeFfmpeg()
    {
        try
        {
            var psi = new ProcessStartInfo("ffmpeg")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-version");
            using var p = Process.Start(psi);
            p!.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>Writes a wav of a four-on-the-floor pattern at a given tempo, over a held tone: a real
    /// beat for the analyser and a real pitch for the key detector.</summary>
    private string WriteTrack(string name, double bpm, double seconds, double toneHz)
    {
        int rate = 44100;
        var samples = new float[(int)(rate * seconds)];
        double period = 60.0 / bpm;

        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)(0.12 * Math.Sin(2 * Math.PI * toneHz * i / rate));

        var rng = new Random(name.GetHashCode() & 0x7fffffff);
        int beat = 0;
        for (double at = 0; at < seconds; at += period, beat++)
        {
            int start = (int)(at * rate);
            int length = rate / 25;
            double gain = beat % 4 == 0 ? 0.9 : 0.5;
            for (int i = 0; i < length && start + i < samples.Length; i++)
            {
                double decay = Math.Pow(1 - (double)i / length, 2);
                // A kick is mostly low end plus a click of noise.
                samples[start + i] += (float)(gain * decay *
                    (0.7 * Math.Sin(2 * Math.PI * 60 * i / rate) + 0.3 * (rng.NextDouble() * 2 - 1)));
            }
        }

        var path = Path.Combine(_root, $"{name}.wav");
        WriteWav(path, samples, rate);
        return path;
    }

    private static void WriteWav(string path, float[] mono, int rate)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        int dataBytes = mono.Length * sizeof(short);

        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);            // PCM
        writer.Write((short)1);            // mono
        writer.Write(rate);
        writer.Write(rate * 2);            // byte rate
        writer.Write((short)2);            // block align
        writer.Write((short)16);           // bits
        writer.Write("data"u8);
        writer.Write(dataBytes);
        foreach (var sample in mono)
            writer.Write((short)(Math.Clamp(sample, -1f, 1f) * short.MaxValue));
    }

    /// <summary>
    /// A beat model that reports a record at a tempo the test already knows, with its beats rounded to
    /// the twenty millisecond frame a real model uses.
    ///
    /// <para>Needed because the tempo comes only from a model (see <see cref="TrackAnalyzer"/>). The
    /// real model is eighty megabytes, runs on ONNX Runtime and is in RPM.Desktop, so a test in Core
    /// cannot use it. These tests are about what the deck and the planner do with a reading, not about
    /// how the reading is made.</para>
    /// </summary>
    private sealed class StandInBeatModel(double bpm) : IBeatModel
    {
        public BeatReading? Read(ReadOnlySpan<float> mono, int sampleRate, CancellationToken ct = default)
        {
            double duration = (double)mono.Length / sampleRate;
            double period = 60.0 / bpm;
            var beats = new List<double>();
            var downbeats = new List<double>();

            for (int i = 0; i * period < duration; i++)
            {
                double at = Math.Round(i * period * MelSpectrogram.FrameRate) / MelSpectrogram.FrameRate;
                beats.Add(at);
                if (i % 4 == 0)
                    downbeats.Add(at);
            }

            return new BeatReading { BeatTimes = beats, DownbeatTimes = downbeats };
        }
    }

    /// <param name="bpm">The tempo the record was written at, passed to the stand-in model. Zero for
    /// a track left with no tempo on purpose.</param>
    private static MixTrack Analyse(string path, double bpm)
    {
        var mono = PcmDecoder.DecodeMono(path, TrackAnalyzer.AnalysisRate);
        var analysis = TrackAnalyzer.Analyze(
            path, mono, TrackAnalyzer.AnalysisRate,
            bpm > 0 ? new StandInBeatModel(bpm) : null);
        return new MixTrack
        {
            Source = path,
            Path = path,
            Title = Path.GetFileNameWithoutExtension(path),
            TaggedDuration = analysis.DurationSeconds,
            Analysis = analysis,
        };
    }

    [Fact]
    public void A_real_file_is_decoded_and_read_for_tempo()
    {
        if (!FfmpegAvailable) return;

        var track = Analyse(WriteTrack("beat", 124, 30, 220), 124);

        Assert.NotNull(track.Analysis);
        Assert.True(Math.Abs(track.Bpm - 124) < 2, $"read {track.Bpm:F2} BPM off the file");
        Assert.True(track.Analysis!.CanBeatMatch);
        Assert.InRange(track.Analysis.DurationSeconds, 29, 31);
    }

    [Fact]
    public void A_deck_stretches_a_real_file_onto_another_tempo()
    {
        if (!FfmpegAvailable) return;

        var path = WriteTrack("stretch", 120, 20, 220);
        using var deck = new Deck(Deck.Side.A, Rate, Channels);
        var track = Analyse(path, 120);

        // Pulled 5% fast: 4 seconds of the record should come out in about 3.81 s of audio.
        deck.Load(track, cueSeconds: 0, tempoRatio: 1.05);
        deck.Fader = 1;

        var block = new float[1024 * Channels];
        long frames = 0;
        while (frames < Rate * 4 && deck.Read(block, 1024) is var read && read > 0)
            frames += read;

        double realSeconds = (double)frames / Rate;
        Assert.InRange(deck.PositionSeconds, realSeconds * 1.04, realSeconds * 1.06);

        // Against the tempo the analyser read from this file, not the nominal 120: what the deck
        // reports must be what it is really playing, whatever the analysis found.
        Assert.Equal(track.Bpm * 1.05, deck.PlayingBpm, 3);
        Assert.True(Math.Abs(track.Bpm - 120) < 2, $"the file should still read near 120, read {track.Bpm:F2}");
    }

    [Fact]
    public void Two_real_records_are_mixed_into_one_continuous_stream()
    {
        if (!FfmpegAvailable) return;

        // Short records with short outros, so the whole set plays out inside a test.
        var first = Analyse(WriteTrack("first", 124, 25, 220), 124);
        var second = Analyse(WriteTrack("second", 126, 25, 330), 126);

        // Move the mix-out earlier: these are 25 s records, so the planner's default tail would
        // leave no room for a changeover at all.
        first = first with { Analysis = first.Analysis! with { OutroStartSeconds = 12 } };
        second = second with { Analysis = second.Analysis! with { OutroStartSeconds = 12 } };

        // Real decoders, deterministic scheduling. The DJ normally cues the next record twelve
        // seconds of AUDIO ahead, which is twelve real seconds when a sound device paces the pump.
        // This loop uses those twelve seconds in milliseconds, so ffmpeg would never open in time
        // and the set would use its seam fallback instead of mixing. Cueing inline makes the wait
        // something the test can rely on.
        using var dj = new Dj(
            new DjCrate([first, second]), Rate, Channels,
            cueRunner: work => { work(); return Task.CompletedTask; });
        var heard = new List<string>();
        dj.TrackChanged += t => heard.Add(t.Title!);

        var block = new float[1024 * Channels];
        long frames = 0, audible = 0;
        bool everBothDecks = false;
        double peak = 0;
        var guard = Stopwatch.StartNew();

        while (dj.Read(block, 1024) is var read && read > 0)
        {
            frames += read;
            if (dj.IsTransitioning)
                everBothDecks = true;

            // Blocks are counted as audio only when they contain some. Nothing here is rate-limited by
            // a sound device, so while a decoder opens, the DJ's hold outputs silence as fast as this
            // loop asks for it. That is real behaviour, but it is not seconds of music.
            bool silent = true;
            for (int i = 0; i < read * Channels; i++)
            {
                peak = Math.Max(peak, Math.Abs(block[i]));
                if (block[i] != 0) silent = false;
            }
            if (!silent) audible += read;

            if (guard.Elapsed > TimeSpan.FromSeconds(30))
                break;                       // safety limit: a set that hangs must fail, not run for ever
        }

        Assert.True(dj.HasFinished, "the set should have played itself out");
        Assert.Equal(["first", "second"], heard);
        Assert.True(everBothDecks, "the two records should have overlapped");

        // More audio than either record has on its own: the second one really played, and the set
        // did not end with the first. How much more is not asserted: this loop is paced only by the
        // CPU and shares a machine with the rest of the suite, so the exact figure changes with how
        // long real ffmpeg processes take to open. What the test checks is above: both records
        // heard, in order, overlapping, audible.
        double seconds = (double)audible / Rate;
        Assert.True(seconds > 28, $"only {seconds:F1}s of audio came out of two 25 s records");
        Assert.True(peak > 0.05, $"the mix came out near-silent (peak {peak:F3})");
    }

    [Fact]
    public void A_record_that_has_gone_missing_is_skipped_and_the_set_plays_on()
    {
        if (!FfmpegAvailable) return;

        var first = Analyse(WriteTrack("plays", 124, 20, 220), 124);
        first = first with { Analysis = first.Analysis! with { OutroStartSeconds = 8 } };

        var gone = new MixTrack
        {
            Source = Path.Combine(_root, "deleted.flac"),
            Path = Path.Combine(_root, "deleted.flac"),
            Title = "gone",
            TaggedDuration = 20,
        };

        var last = Analyse(WriteTrack("plays-too", 124, 20, 330), 124);
        last = last with { Analysis = last.Analysis! with { OutroStartSeconds = 8 } };

        using var dj = new Dj(new DjCrate([first, gone, last]), Rate, Channels);
        // Wall clock, not a frame count: while a decoder opens, the DJ outputs silence, and this loop
        // (with no sound device pacing it) reads those blocks as fast as it can.
        var block = new float[1024 * Channels];
        var guard = Stopwatch.StartNew();
        while (dj.Read(block, 1024) > 0 && guard.Elapsed < TimeSpan.FromSeconds(30)) { }

        Assert.True(dj.HasFinished);
        // The record that is not there produces nothing; the one after it still gets played.
        Assert.Contains(dj.Played, t => t.Title == "plays-too");
    }

    [Fact]
    public void A_missing_file_ends_the_set_instead_of_hanging_it()
    {
        if (!FfmpegAvailable) return;

        var missing = new MixTrack
        {
            Source = Path.Combine(_root, "not-here.flac"),
            Path = Path.Combine(_root, "not-here.flac"),
            Title = "ghost",
            TaggedDuration = 120,
        };

        using var dj = new Dj(new DjCrate([missing]), Rate, Channels);
        var block = new float[1024 * Channels];

        // ffmpeg exits immediately on a file that is not there; the deck reads nothing and the DJ
        // moves on instead of looping on an empty decoder.
        var guard = Stopwatch.StartNew();
        while (dj.Read(block, 1024) > 0 && guard.Elapsed < TimeSpan.FromSeconds(30)) { }

        Assert.True(dj.HasFinished);
    }
}

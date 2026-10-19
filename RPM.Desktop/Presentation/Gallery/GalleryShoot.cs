#if DEBUG
using RPM.Core.Models;
using RPM.Desktop.Presentation.RPaMp;
using RPM.Desktop.Presentation.Viz;

namespace RPM.Desktop.Presentation.Gallery;

/// <summary> Opens a visualizer full screen, with something playing, to take the gallery card stills.
///
/// <para>Every visualizer in the catalogue is shown to the user as a photograph of itself. Taking
/// one by hand needs a real library, a real record and a person taking the picture. The pictures come
/// out at whatever size and moment the person catches, so they are not quite the same shape. This makes
/// the shot repeatable: one command, the same track, the same cover, and the same seconds in.</para>
///
/// <para><b>Why it plays real audio.</b> The rest of the harness works with material that only has to
/// be looked at. A made-up path fills a queue well, because a queue is a list. A visualizer does not
/// read the library. It listens to the speakers, and a stage with silence going into it draws a flat
/// line and a still room. So a few seconds of PCM are written to the temp folder and actually played: a
/// kick, a bass line, a pad and a hat. They are spread across the whole range, so every part of the
/// spectrum has something in it, and the low end has a pulse to swell on. It is not music. It is a test
/// tone that photographs well.</para>
///
/// <para>The cover beside it is drawn by <see cref="GalleryArt"/>, the same way the demo library's
/// albums are drawn. So a stage that paints itself in the record's colours has real colours to
/// find.</para>
///
/// <para>DEBUG only, and only on <c>--gallery --shoot=&lt;id&gt;</c>. Nothing here is reachable from a
/// normal run, let alone a Release build.</para> </summary>
internal static class GalleryShoot
{
    /// <summary>The value given to <c>--shoot=</c>, as written: a catalogue id, optionally followed by
    /// <c>:&lt;view&gt;</c> naming a Pegasus composition. It is null when the switch was not given, and
    /// then this whole file does nothing.</summary>
    public static string? Wanted { get; set; }

    /// <summary>A picture to shoot against, from <c>--shoot-cover=&lt;path&gt;</c>. Null uses the
    /// made-up cover that the rest of the harness draws.</summary>
    public static string? Cover { get; set; }

    private const string Artist = "Boards of Canada";
    private const string Album = "Music Has the Right to Children";
    private const string Title = "Roygbiv";

    /// <summary>How long the written clip runs. It is long enough that it cannot end while the shot is
    /// being lined up, and short enough to write in well under a second.</summary>
    private const double ClipSeconds = 90.0;

    /// <summary>The name that <c>--shoot=pegasus</c> uses. It is not a catalogue id, because the
    /// full-screen player is a view. The shoot still needs a still of it, and this is the name it goes
    /// by.</summary>
    private const string PegasusId = "pegasus";

    /// <summary>How often the launch is checked, and how long it is given before the shoot is
    /// abandoned. A cold gallery run has a library to seed and a shell to build, and that can take
    /// twenty seconds. So this waits for the shell rather than using a fixed delay. A fixed four-second
    /// delay is too short for a cold run, and the shoot would then do nothing without any
    /// sign.</summary>
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(500);
    private const int GiveUpTicks = 160;          // 80 seconds

    /// <summary>How many polls to leave between starting the track and going full screen. The file has
    /// to be opened and decoded, the cover read, and its colours found. The stage's own eases (the
    /// room's colour, and the record coming up to speed) take a second or so each.</summary>
    private const int SettleTicks = 8;            // 4 seconds

    /// <summary>Puts the named visualizer up with the clip playing under it. It is called once from the
    /// launch path. It waits for the shell itself, so it does not matter how early that is.</summary>
    public static void Fire()
    {
        if (Wanted is not { Length: > 0 } asked)
            return;

        // The id is given as "<id>" or "<id>:<view>". A view that does not parse falls back to the
        // visualizer's own default rather than being refused. The id is what the shoot is for.
        var cut = asked.IndexOf(':');
        var vizId = cut < 0 ? asked : asked[..cut];
        PegasusView? openOn = cut >= 0 && Enum.TryParse<PegasusView>(asked[(cut + 1)..], ignoreCase: true, out var v)
            ? v
            : null;

        var track = Material();
        var dispatcher = App.Services.GetRequiredService<Microsoft.UI.Dispatching.DispatcherQueue>();

        int ticks = 0, playedAt = -1;
        Services.Shell.Ticker.Every(dispatcher, Poll, () =>
        {
            if (++ticks > GiveUpTicks)
                return false;

            // Nothing can be driven until the shell is up. The shell owns the visualizer overlay and
            // the art display that resolves the cover. The stage takes its colours from that cover.
            if (ShellPage.Current is not { } shell)
                return true;

            if (playedAt < 0)
            {
                App.Services.GetRequiredService<RPaMpService>().Transport.PlayNow(track);
                playedAt = ticks;
                return true;
            }

            if (ticks - playedAt < SettleTicks)
                return true;

            // Pegasus is a VIEW rather than a gallery entry (see ViewModeService). So it is asked for
            // by name here, instead of being looked up in the catalogue it left. Everything else is a
            // card in the Visualizations pane, and it goes up the ordinary way.
            if (vizId == PegasusId)
                App.Services.GetRequiredService<ViewModeService>().ShowFullScreen(openOn);
            else
                shell.ShowVisualizer(vizId);

            return false;
        });
    }

    /// <summary>Writes the clip and its cover, and describes them as a track. Both files are left in
    /// the temp folder between runs. The bytes are the same every time, so two shoots of the same
    /// visualizer differ only in the moment they were taken.</summary>
    private static TrackInfo Material()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rpm-gallery-shoot");
        Directory.CreateDirectory(dir);

        // The cover goes in as a folder picture. That is the ordinary route a record's art travels, so
        // the art display resolves it exactly as it would for anything else. The colours the stage
        // paints with are read off the same bytes, on the same pass. A cover named on the command line
        // is copied in on every run and never kept. The point of naming one is to change which record
        // the room is lit by. A cached first choice would make the second run show the first cover.
        var cover = Path.Combine(dir, "cover.jpg");
        if (Cover is { Length: > 0 } given && File.Exists(given))
            File.Copy(given, cover, overwrite: true);
        else if (!File.Exists(cover))
            File.WriteAllBytes(cover, GalleryArt.CoverJpeg(Artist, Album));

        var wav = Path.Combine(dir, $"{Artist} - {Title}.wav");
        if (!File.Exists(wav))
            File.WriteAllBytes(wav, Clip());

        var track = new TrackInfo
        {
            FilePath = wav,
            Extension = ".wav",
            DurationSeconds = ClipSeconds,
            Title = Title,
            Artist = Artist,
            AlbumArtist = Artist,
            Album = Album,
            Year = 1998,
            Source = MetadataSource.Tags,
        };

        return track;
    }

    // ── the clip ──

    /// <summary> A few bars of tone, written straight out as 16-bit stereo PCM.
    ///
    /// <para>It is composed for a spectrum, not for an ear. Every band of the run has a voice in it. A
    /// stage that measures each third of the range against its own recent peak will raise an empty
    /// third until it draws the noise floor. That looks like a fault. The kick is on its own at the
    /// bottom, so the low end has a pulse rather than a flat level. The figures swell on that
    /// pulse.</para> </summary>
    /// <param name="seconds">How much of it to write. The shoot takes the whole <see
    /// cref="ClipSeconds"/>, because the run has to still be going when the shutter falls. The demo
    /// library asks for a shorter clip, because the record it stands in for has to end while somebody
    /// is still watching (see GalleryBootstrap). Nothing about the material depends on the length. It
    /// is a loop.</param>
    internal static byte[] Clip(double seconds = ClipSeconds)
    {
        const int rate = RPM.Desktop.Services.Audio.AudioEngine.Rate;
        int frames = (int)(rate * seconds);

        var pcm = new byte[frames * 4];              // stereo, 16-bit
        var noise = new Random(0x5EED);              // seeded, so two runs write the same clip

        // Two one-pole high passes in series, each carrying its own state down the loop. White noise is
        // flat. Flat noise would put as much into the bottom of the run as the kick does. That would
        // leave the low end a level rather than a pulse, and the figures would stop moving. Cascaded,
        // the corner is steep enough that what is left is air.
        double hp1 = 0, hp2 = 0, prev1 = 0, prev2 = 0;

        for (int n = 0; n < frames; n++)
        {
            double t = n / (double)rate;
            double beat = t * 2.0;                   // 120 bpm
            double intoBeat = beat - Math.Floor(beat);

            // The kick: 55 Hz with a fast decay, and a pitch drop through the attack, so it reads as a
            // drum rather than a tone. It sits alone at the bottom of the range.
            double kickEnv = Math.Exp(-intoBeat * 9.0);
            double kick = Math.Sin(2 * Math.PI * (55 + 40 * kickEnv) * t) * kickEnv * 0.55;

            // A bass line under it. It changes note every two beats, so the bottom of the run moves.
            double[] bassHz = [82.4, 82.4, 110.0, 92.5];
            double bass = Math.Sin(2 * Math.PI * bassHz[(int)(beat / 2) % 4] * t) * 0.20;

            // The middle: a slow triad. This is where a voice and most of an arrangement would sit.
            double pad = (Math.Sin(2 * Math.PI * 329.6 * t)
                        + Math.Sin(2 * Math.PI * 415.3 * t)
                        + Math.Sin(2 * Math.PI * 493.9 * t)) * 0.055
                        * (0.6 + 0.4 * Math.Sin(2 * Math.PI * 0.15 * t));

            // Upper middle, with tremolo, so the run is not flat between the pad and the air above it.
            double upper = Math.Sin(2 * Math.PI * 1318.5 * t) * 0.045
                         * (0.5 + 0.5 * Math.Sin(2 * Math.PI * 3.0 * t));

            // The hat: white noise through a one-pole high pass, on the off-beat. This is all the top
            // third of the run has to measure itself against.
            double hatEnv = Math.Exp(-((intoBeat + 0.5) % 1.0) * 42.0);
            double white = noise.NextDouble() * 2 - 1;

            const double a = 0.90;                   // y = a(y' + x - x'), about 800 Hz a stage
            hp1 = a * (hp1 + white - prev1); prev1 = white;
            hp2 = a * (hp2 + hp1 - prev2); prev2 = hp1;

            double air = hp2 * hatEnv * 0.16;

            double s = kick + bass + pad + upper + air;
            s = Math.Clamp(s, -1.0, 1.0);

            short v = (short)(s * short.MaxValue * 0.85);
            int i = n * 4;
            pcm[i] = (byte)(v & 0xFF);
            pcm[i + 1] = (byte)((v >> 8) & 0xFF);
            pcm[i + 2] = pcm[i];
            pcm[i + 3] = pcm[i + 1];
        }

        return Wav(pcm, rate);
    }

    /// <summary>The 44-byte canonical WAV header, followed by the samples.</summary>
    private static byte[] Wav(byte[] pcm, int rate)
    {
        using var ms = new MemoryStream(pcm.Length + 44);
        using var w = new BinaryWriter(ms);

        const short channels = 2, bits = 16;
        int byteRate = rate * channels * bits / 8;

        w.Write("RIFF"u8);
        w.Write(36 + pcm.Length);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);                       // PCM chunk size
        w.Write((short)1);                 // PCM
        w.Write(channels);
        w.Write(rate);
        w.Write(byteRate);
        w.Write((short)(channels * bits / 8));
        w.Write(bits);
        w.Write("data"u8);
        w.Write(pcm.Length);
        w.Write(pcm);
        w.Flush();

        return ms.ToArray();
    }
}
#endif

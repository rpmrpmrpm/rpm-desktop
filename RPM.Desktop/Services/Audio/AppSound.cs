using System.Diagnostics;

namespace RPM.Desktop.Services.Audio;

/// <summary>
/// The sound the app makes when it opens.
///
/// <para>
/// Played by the machine's own player, in a separate process. It does not use the player's sound
/// device, and this is required. Both use OpenAL, and a source belongs to the context it was made in.
/// The current context is one process-wide value. If the sound shared the player's device, stopping
/// it when a record starts would also stop and delete the player's source, and the music would go
/// silent. Keeping the sound in its own process means it shares nothing with the music except the
/// speakers.
/// </para>
///
/// <para>The level is kept by writing the samples out at the level they should be heard, rather than
/// by asking the platform's player for a volume it may not have. One temp file, deleted after.</para>
///
/// <para>Everything here is best effort and silent about failure. A machine with no player command, a
/// missing file, a temp directory that will not take a write: none of that is a reason to hold up a
/// window, and none of it is worth a word to somebody who has just opened a music player.</para>
/// </summary>
public static class AppSound
{
    /// <summary>Where the opening sound is, beside the binary.</summary>
    private static string OpenPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", "rpm-open.wav");

    /// <summary>The sound's level relative to the player's volume. It is quieter than the music,
    /// because the sound plays while the window opens, not while music is playing.</summary>
    private const double Softer = 0.7;

    /// <summary>How often the sound is checked to see if it should stop. The check is frequent enough
    /// that pressing play silences the sound at once, and cheap enough to cost nothing.</summary>
    private const int PollMs = 50;

    /// <summary>
    /// Plays it, on a thread of its own.
    /// </summary>
    /// <param name="volume">The player's volume, 0 to 100. The sound is played under it.</param>
    /// <param name="stop">Checked while the sound plays. When it returns true, the sound stops. A
    /// track started by the user takes priority over the sound.</param>
    public static void PlayOpen(int volume = 100, Func<bool>? stop = null) =>
        Play(OpenPath, volume, stop);

    /// <summary>Ends the opening sound now. This covers the ways the app can exit while the sound is
    /// still playing. A child process does not stop when its parent stops. On macOS, closing the window
    /// does not end the process. Both cases are handled here.</summary>
    public static void Silence()
    {
        var player = Interlocked.Exchange(ref _playing, null);
        try { if (player is { HasExited: false }) player.Kill(); } catch { }
    }

    private static Process? _playing;

    private static void Play(string path, int volume, Func<bool>? stop)
    {
        if (!File.Exists(path))
            return;

        // A dedicated thread, not a pool thread. It waits on a child process for the length of the
        // sound, and a pool thread blocked that long could hold up startup work. The thread is a
        // background thread, so it never keeps the app open.
        new Thread(() =>
        {
            string? temp = null;
            try
            {
                if (Wave.Read(path) is not { Frames: > 0 } wave)
                    return;

                temp = Wave.WriteScaled(wave, Math.Clamp(volume, 0, 100) / 100.0 * Softer);
                if (temp is null || Command(temp) is not { } start)
                    return;

                // Checked once more before the player starts. Reading and rewriting the file takes a
                // moment. If the user has already started a track, the sound is not played.
                if (stop?.Invoke() == true)
                    return;

                using var player = Process.Start(start);
                if (player is null)
                    return;

                try
                {
                    Interlocked.Exchange(ref _playing, player);
                    AppDomain.CurrentDomain.ProcessExit += OnAppExit;

                    // The process is killed, not faded out. The sound is stopped because the speakers
                    // are needed for a track that has already started.
                    while (!player.WaitForExit(PollMs))
                    {
                        if (stop?.Invoke() != true) continue;
                        try { player.Kill(); } catch { }
                        break;
                    }
                }
                finally
                {
                    Interlocked.CompareExchange(ref _playing, null, player);
                    AppDomain.CurrentDomain.ProcessExit -= OnAppExit;
                }
            }
            catch
            {
                // No player on this machine, no file, or a temp directory that refuses the write. The
                // app opens either way.
            }
            finally
            {
                try { if (temp is not null) File.Delete(temp); } catch { }
            }
        })
        { IsBackground = true, Name = "app-sound" }.Start();
    }

    private static void OnAppExit(object? sender, EventArgs e) => Silence();

    /// <summary>The simplest command on this machine that plays a wav file and then stops.
    ///
    /// <para>No volume is passed. The file is already at the right level, because the platform players
    /// do not agree on how to set a volume, and one of them cannot set one.</para>
    /// </summary>
    private static ProcessStartInfo? Command(string file)
    {
        if (OperatingSystem.IsMacOS())
            return Quiet("/usr/bin/afplay", file);

        if (OperatingSystem.IsWindows())
            return Quiet("powershell.exe",
                         "-NoProfile", "-WindowStyle", "Hidden", "-Command",
                         $"(New-Object Media.SoundPlayer '{file.Replace("'", "''")}').PlaySync()");

        // Uses whichever of these the machine has. A desktop Linux usually has one of them. A machine
        // with neither plays no sound.
        foreach (var player in new[] { "paplay", "aplay" })
            if (Which(player) is { } found)
                return Quiet(found, file);

        return null;
    }

    private static ProcessStartInfo Quiet(string program, params string[] arguments)
    {
        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    /// <summary>Where a command is, or null if the machine has not got it.</summary>
    private static string? Which(string program)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'))
        {
            if (directory.Length == 0) continue;
            var candidate = Path.Combine(directory, program);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}

/// <summary>
/// Enough of RIFF/WAVE to read and write the app's own sounds: 16-bit PCM, which is what they are
/// shipped as.
///
/// <para>This code reads the file directly. The rest of the app decodes audio with ffmpeg. This reader
/// runs while the window opens, and starting a process and a decoder would slow startup for a sound
/// that is shipped in one known format.</para>
/// </summary>
public static class Wave
{
    /// <param name="Samples">Interleaved, -1 to 1, as the sink takes them.</param>
    /// <param name="Frames">One frame is one sample per channel.</param>
    public sealed record Pcm(float[] Samples, int Frames, int SampleRate, int Channels)
    {
        public double Seconds => SampleRate > 0 ? (double)Frames / SampleRate : 0;
    }

    /// <summary>Writes the same sound at a lower level, to a file the platform player can open. Returns
    /// the path, or null if the file could not be written.
    ///
    /// <para>The level is written into the samples. The platform players do not agree on how to set a
    /// volume, and one of them cannot set one. Every player can play a file as it is, so that is what
    /// is done.</para>
    /// </summary>
    public static string? WriteScaled(Pcm sound, double gain)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), $"rpm-open-{Environment.ProcessId}.wav");

            using var file = File.Create(path);
            using var writer = new BinaryWriter(file);

            int bytes = sound.Frames * sound.Channels * 2;
            int rate = sound.SampleRate, channels = sound.Channels;

            writer.Write("RIFF"u8);
            writer.Write(36 + bytes);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);                             // the size of the format chunk itself
            writer.Write((ushort)1);                      // PCM
            writer.Write((ushort)channels);
            writer.Write(rate);
            writer.Write(rate * channels * 2);            // bytes per second
            writer.Write((ushort)(channels * 2));         // block align
            writer.Write((ushort)16);                     // bits per sample
            writer.Write("data"u8);
            writer.Write(bytes);

            foreach (var sample in sound.Samples.AsSpan(0, sound.Frames * channels))
                writer.Write((short)(Math.Clamp(sample * gain, -1, 1) * short.MaxValue));

            return path;
        }
        catch { return null; }
    }

    public static Pcm? Read(string path)
    {
        using var file = File.OpenRead(path);
        using var reader = new BinaryReader(file);

        // Read the four-byte IDs as raw bytes, not as characters. BinaryReader reads ahead through
        // a buffer when it decodes characters. The stream position used by this walk would then be
        // ahead of where the code expects it.
        string Id() => System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4));

        if (Id() != "RIFF")
            return null;
        reader.ReadUInt32();                                  // the size of everything after it
        if (Id() != "WAVE")
            return null;

        int channels = 0, sampleRate = 0, bits = 0;

        // Walk the chunks until the data chunk. Editors often add LIST, fact and other chunks between
        // the format and the samples, so these are skipped.
        while (file.Position + 8 <= file.Length)
        {
            var id = Id();
            var size = reader.ReadUInt32();

            // Where the next chunk starts. Chunks are word-aligned, so an odd-sized chunk has a pad
            // byte that its size does not include. The bounds are checked before reading, because a
            // corrupt size would seek past the end of the file.
            var next = file.Position + size + (size % 2);
            if (next > file.Length)
                return null;

            if (id == "fmt ")
            {
                reader.ReadUInt16();                          // format tag; PCM or the extensible one
                channels = reader.ReadUInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadUInt32();                          // bytes per second
                reader.ReadUInt16();                          // block align
                bits = reader.ReadUInt16();
            }
            else if (id == "data")
            {
                if (channels <= 0 || sampleRate <= 0 || bits != 16)
                    return null;                              // not a supported format

                var bytes = reader.ReadBytes((int)Math.Min(size, int.MaxValue));
                var frames = bytes.Length / (2 * channels);
                var samples = new float[frames * channels];

                for (int i = 0; i < samples.Length; i++)
                    samples[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;

                return new Pcm(samples, frames, sampleRate, channels);
            }

            file.Position = next;
        }

        return null;
    }
}

using System.Diagnostics;
using System.Globalization;
namespace RPM.Fingerprint;

/// <summary>
/// The renditions a blob is served as, and the head clip that lets playback start at once.
///
/// <para>
/// Both come from one pass and are parts of the same feature. A <em>head clip</em> is the first few
/// seconds of a track, small enough that every track on an album can be kept in memory, so that
/// skipping through a record starts each track at once, not only the next two. A
/// <em>rendition</em> is the whole track at a chosen rate, so that a phone on a weak connection can
/// play the album, and so that lossless can be a paid plan feature.
/// </para>
///
/// <para>
/// AAC-LC in a faststart MP4, for both. Not because it is the best codec per bit (Opus is), but
/// because it is the only codec that iOS, Android, browsers and Chromecast all decode natively, and
/// this audio must play on all four. Faststart moves the <c>moov</c> atom to the front of the file,
/// so a decoder can start on the first bytes instead of after the last. A head clip depends on this:
/// an ordinary MP4 with its index at the end cannot be decoded from a prefix.
/// </para>
///
/// <para>
/// Next to <see cref="ToolPaths"/>, not in RPM.Core, because two very different programs run it: the
/// desktop, which makes renditions while it uploads, and the transcoding Lambda, which makes the ones
/// no desktop can make. Neither may bring in the other's dependencies.
/// </para>
/// </summary>
public static class Transcoding
{
    /// <summary>
    /// The first <see cref="AudioLadder.HeadSeconds"/> of a track, as bytes ready to be stored and played.
    /// </summary>
    /// <param name="path">The canonical blob, or any file ffmpeg can read.</param>
    public static byte[] Head(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var to = Path.Combine(Path.GetTempPath(), $"gr-head-{Guid.NewGuid():N}.m4a");

        try
        {
            Run(path, to, AudioLadder.HeadKilobits, AudioLadder.HeadSeconds);
            return File.ReadAllBytes(to);
        }
        finally
        {
            Delete(to);
        }
    }

    /// <summary>The whole track at one rate on the ladder, written to <paramref name="to"/>.</summary>
    public static void Rendition(string path, string to, int kilobits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(to);

        if (!AudioLadder.IsOnTheLadder(kilobits))
            throw new ArgumentOutOfRangeException(nameof(kilobits), kilobits, "not a rate on the ladder");

        Run(path, to, kilobits, seconds: 0);
    }

    /// <summary>The whole track at one rate, as bytes. This is what the uploader PUTs.</summary>
    public static byte[] RenditionBytes(string path, int kilobits)
    {
        var to = Path.Combine(Path.GetTempPath(), $"gr-rate-{Guid.NewGuid():N}.m4a");

        try
        {
            Rendition(path, to, kilobits);
            return File.ReadAllBytes(to);
        }
        finally
        {
            Delete(to);
        }
    }

    /// <summary>
    /// The whole track at one rate, written to <paramref name="to"/> while it is encoded, and kept as
    /// a file for the next listener.
    ///
    /// <para>
    /// This path is used when someone asks for a rung that does not exist yet and there is no lower
    /// rung. When a lower rung exists, that one is served and the requested rung is built for next
    /// time. Here the only other choice would be to make the listener wait.
    /// </para>
    ///
    /// <para>
    /// <b>ADTS to the listener, a faststart MP4 to the store, and one encode for both.</b> An MP4 with
    /// its index at the front cannot be written to a pipe (ffmpeg seeks backwards to put it there),
    /// and an MP4 without it cannot be decoded until its last byte arrives. ADTS has its format in
    /// every frame, so a decoder can start on the first bytes. The stored file must be the same MP4
    /// that the desktop and the backfill make, because every later play is served from it. So the
    /// bytes are saved as they are sent and remuxed at the end: a copy, not a second encode.
    /// </para>
    ///
    /// <para>
    /// <b>Nothing is kept unless the encode finished.</b> The store's rule is that a rate which is
    /// present is complete: the API reads the rate set and signs a URL without checking the object.
    /// A partial file kept here would be a rendition that stops in the middle, permanently, for every
    /// user whose upload is deduplicated onto that blob. A listener whose stream fails during the
    /// encode gets a truncated stream and a retry, which is much less harmful.
    /// </para>
    /// </summary>
    /// <param name="to">Where the audio is written while it is encoded. The response stream, in the
    /// Lambda.</param>
    /// <param name="startingAt">Which second to begin at: a seek during the first play, when there
    /// is no file to range-read yet. Required, with no default, because it decides whether the output
    /// may be kept.</param>
    /// <returns>The path of the faststart MP4 to store, which the caller owns and deletes, or null
    /// for a pass that did not start at the beginning, which is not a complete rendition.</returns>
    public static async Task<string?> LiveAsync(
        string from, Stream to, int kilobits, double startingAt, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        ArgumentNullException.ThrowIfNull(to);

        if (!AudioLadder.IsOnTheLadder(kilobits))
            throw new ArgumentOutOfRangeException(nameof(kilobits), kilobits, "not a rate on the ladder");

        var stem = Path.Combine(Path.GetTempPath(), $"gr-live-{Guid.NewGuid():N}");
        var adts = $"{stem}.aac";
        var mp4 = $"{stem}.m4a";

        // An encode that starts partway through has no beginning, and the store's rule is that a
        // rate which is present is complete. If stored, it would be a rendition that starts in the
        // middle, permanently, for every user deduplicated onto that blob. So a seek is served and
        // not stored, and the rung is made later by a play that starts at the beginning.
        var whole = startingAt <= 0;

        try
        {
            await PipeAsync(from, to, adts, kilobits, startingAt, ct);

            if (!whole) return null;

            // A remux, so the rate, the resampling and the channel count are the ones set above,
            // and what is stored is the same audio that was sent.
            Remux(adts, mp4);

            return mp4;
        }
        catch
        {
            Delete(mp4);
            throw;
        }
        finally
        {
            Delete(adts);
        }
    }

    /// <summary>Encodes to a pipe, and writes every byte to two places: to the listener and to a file
    /// for the remux. ffmpeg runs only while the pipe is read (a full pipe stops it), so the slower
    /// of the two readers sets the speed, which is the listener's connection.</summary>
    private static async Task PipeAsync(
        string from, Stream to, string adts, int kilobits, double startingAt, CancellationToken ct)
    {
        var psi = Encode(from, kilobits, seconds: 0, startingAt);

        // ADTS, not MP4, and stdout, not a path. See LiveAsync.
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("adts");
        psi.ArgumentList.Add("pipe:1");
        psi.RedirectStandardOutput = true;

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start ffmpeg for a live transcode");

        // Read on its own task from the start. ffmpeg writes errors as it runs, and a full error
        // pipe stops the process, which leaves the listener on a stream that no longer receives
        // data, with no error.
        var complaints = proc.StandardError.ReadToEndAsync(ct);

        await using (var kept = new FileStream(
            adts, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 16, useAsync: true))
        {
            var buffer = new byte[1 << 16];

            while (true)
            {
                var read = await proc.StandardOutput.BaseStream.ReadAsync(buffer, ct);
                if (read == 0) break;

                var block = buffer.AsMemory(0, read);

                // The listener first. The stored copy is for the next listener and can wait a
                // millisecond; the current listener is waiting for audio.
                await to.WriteAsync(block, ct);
                await to.FlushAsync(ct);
                await kept.WriteAsync(block, ct);
            }
        }

        await proc.WaitForExitAsync(ct);

        var said = await complaints;

        if (proc.ExitCode != 0 || new FileInfo(adts).Length == 0)
            throw new FfmpegFailedException(
                $"ffmpeg could not transcode to {kilobits}k: {FfmpegSaid.Short(said)}", said);
    }

    /// <summary>The same encoded audio in the container the store keeps. <c>-c copy</c>: the frames
    /// are not changed, only rewrapped, so the stored audio is exactly the audio that was sent.</summary>
    private static void Remux(string adts, string mp4)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ToolPaths.Ffmpeg,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(adts);
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("copy");
        psi.ArgumentList.Add("-movflags"); psi.ArgumentList.Add("+faststart");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("mp4");
        psi.ArgumentList.Add(mp4);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start ffmpeg for a remux");

        var said = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode != 0 || !File.Exists(mp4) || new FileInfo(mp4).Length == 0)
            throw new FfmpegFailedException(
                $"ffmpeg could not rewrap the rendition: {FfmpegSaid.Short(said)}", said);
    }

    /// <summary>
    /// Every setting that decides what the audio IS, and nothing about its container.
    ///
    /// <para>Shared because the live path and the stored path must agree exactly: they are the same
    /// rendition of the same blob at the same rate, one played now and one kept for the next
    /// listener. A flag that differed between them would give two different files under one
    /// key.</para>
    /// </summary>
    /// <param name="seconds">How much to encode, or zero for all of it.</param>
    /// <param name="startingAt">Which second to begin at, or zero for the start of the track.</param>
    private static ProcessStartInfo Encode(string path, int kilobits, double seconds, double startingAt = 0)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ToolPaths.Ffmpeg,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-y");

        // Before the input, so it is a seek, not a decode-and-discard: ffmpeg jumps in the container
        // instead of decoding up to that point, so starting forty minutes into a long file costs the
        // same as starting at the beginning.
        if (startingAt > 0)
        {
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(startingAt.ToString("F3", CultureInfo.InvariantCulture));
        }

        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(path);

        if (seconds > 0)
        {
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add(seconds.ToString("F2", CultureInfo.InvariantCulture));
        }

        // The audio only, and nothing copied from the source: no cover art in a clip that should be
        // about 36 KB, and no tags, because a rendition is keyed by content hash and shared between
        // users who must not get each other's metadata.
        psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("0:a:0");
        psi.ArgumentList.Add("-map_metadata"); psi.ArgumentList.Add("-1");
        psi.ArgumentList.Add("-vn");

        psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("aac");
        psi.ArgumentList.Add("-b:a"); psi.ArgumentList.Add($"{kilobits}k");
        psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add("2");

        // Always 44.1 kHz. A rendition at the 96 kHz of a master would be several times larger with
        // no difference a phone speaker can play, and the head clip must have the same sample rate
        // as the stream that follows it.
        psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add("44100");

        return psi;
    }

    private static void Run(string path, string to, int kilobits, double seconds)
    {
        var psi = Encode(path, kilobits, seconds);

        // The index at the front. This is the setting that makes a prefix of the file playable,
        // which the head clip needs.
        psi.ArgumentList.Add("-movflags"); psi.ArgumentList.Add("+faststart");

        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("mp4");
        psi.ArgumentList.Add(to);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start ffmpeg for a transcode");

        var said = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode != 0 || !File.Exists(to) || new FileInfo(to).Length == 0)
            throw new FfmpegFailedException(
                $"ffmpeg could not transcode to {kilobits}k: {FfmpegSaid.Short(said)}", said);
    }

    private static void Delete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* a temp file the OS will take */ }
    }

    /// <summary>
    /// Whether an MP4's index is at the front, which is what <c>+faststart</c> does. This is the one
    /// property of these files that is checked instead of trusted.
    ///
    /// <para>An MP4 is a flat list of boxes, each a four-byte big-endian length and a four-byte name.
    /// Reading the top level is enough: if <c>moov</c> comes before <c>mdat</c>, a decoder can start
    /// on the first bytes of the file, and a head clip works. If not, the clip cannot be decoded
    /// until its last byte arrives, and head clips do not work.</para>
    /// </summary>
    public static bool StartsFast(ReadOnlySpan<byte> file)
    {
        var at = 0;

        while (at + 8 <= file.Length)
        {
            long size = (file[at] << 24) | (file[at + 1] << 16) | (file[at + 2] << 8) | file[at + 3];
            var name = System.Text.Encoding.ASCII.GetString(file.Slice(at + 4, 4));

            if (name == "moov") return true;
            if (name == "mdat") return false;

            // 1 means the real size is a 64-bit value after the name; 0 means "to the end of the
            // file", which can only be the last box.
            if (size == 1)
            {
                if (at + 16 > file.Length) return false;

                size = 0;
                for (var b = 0; b < 8; b++) size = (size << 8) | file[at + 8 + b];
            }
            else if (size == 0)
            {
                return false;
            }

            if (size < 8) return false;

            at += (int)size;
        }

        return false;
    }
}

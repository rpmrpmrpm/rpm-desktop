using System.Diagnostics;
using System.Security.Cryptography;
using RPM.Fingerprint;

namespace RPM.Core.Cloud;

/// <summary>What a file reduces to for the cloud store: its tag-free audio, and the hash of it.</summary>
/// <param name="Path">The canonical file on disk. It is a temp file that the caller owns and
/// deletes.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of that file. This is the blob's key in the store.</param>
/// <param name="SizeBytes">Size of the canonical file, which is what S3 will hold.</param>
public sealed record CanonicalFile(string Path, string Sha256, long SizeBytes);

/// <summary>Reduces an audio file to the bytes that are actually the music. Two users who hold the same
/// encoding of the same track then store one object between them, not two.
///
/// <para>Hashing the file itself would remove no duplicates. Every file in an RPM library carries a unique
/// <see cref="Metadata.RpmTag"/> GUID in its own tags. So two people who downloaded the byte-identical mp3
/// hold two different files. The tags differ, the art differs, and the play counts differ. The audio does
/// not.
/// </para>
///
/// <para>So the unit of storage is the file, with every tag container removed, and nothing re-encoded.
/// <c>-c copy</c> moves the frames across untouched. <c>-map_metadata -1</c> drops the metadata.
/// <c>-bitexact</c> stops the muxer writing its own version string and timestamps. The audio bitstream is
/// bit-identical to the original. The result is deterministic, so the same input gives the same hash on any
/// machine. It is still a valid, playable file. So a cloud track can stream straight into <c>ffmpeg -i
/// &lt;presigned url&gt;</c>, with no reassembly step.
/// </para>
///
/// <para>It does not remove duplicates from two separate rips of the same CD. A different encoder or
/// different settings gives different bytes. Only identical encodings are merged.
/// </para>
/// </summary>
public static class CanonicalAudio
{
    /// <summary>Can be overridden for tests. It defaults to the bundled ffmpeg, then the lookup on
    /// PATH.</summary>
    public static string FfmpegPath { get; set; } = ToolPaths.Ffmpeg;

    /// <summary>Writes the canonical form of <paramref name="sourcePath"/> into
    /// <paramref name="workingDir"/>, and hashes it. The caller owns the returned file and must delete it.
    /// The upload needs the file twice: once for the bytes, and once to answer a proof-of-possession
    /// challenge. So it is not cleaned up here.
    /// </summary>
    /// <exception cref="InvalidOperationException">ffmpeg is missing, or refused the file.</exception>
    public static async Task<CanonicalFile> CreateAsync(
        string sourcePath, string workingDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(workingDir);

        // The same container as the source. The canonical file has to stay playable, and a stream copy can
        // only be muxed into a format that accepts the codec it already is.
        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        var target = Path.Combine(workingDir, $"canon-{Guid.NewGuid():N}{ext}");

        var error = await RunAsync(sourcePath, target, ext, forceMp4: false, ct);

        // The extension chooses the muxer. For .m4a, that muxer is ffmpeg's "ipod" muxer, which accepts a
        // short list of codecs: AAC, ALAC, AC-3, and nothing else. An .m4a that carries Dolby Digital Plus
        // (as a surround or Atmos download does) makes it refuse to write a header at all, and the whole
        // album fails to sync. The generic mp4 muxer accepts everything the container can legally hold, so
        // it gets the second try.
        //
        // It is the second try, not the first. The muxer that wrote the file shows in its bytes. Switching
        // every .m4a to the generic muxer would change the hash of every blob already in the store, and
        // re-upload libraries that are already there. Only the files that cannot be written the old way
        // move to the new one.
        if (error is not null && IsMp4Family(ext) && RefusedTheCodec(error))
            error = await RunAsync(sourcePath, target, ext, forceMp4: true, ct);

        if (error is not null)
        {
            TryDelete(target);
            throw new FfmpegFailedException(
                $"ffmpeg could not canonicalize '{Path.GetFileName(sourcePath)}': {FfmpegSaid.Short(error)}", error);
        }

        if (!File.Exists(target) || new FileInfo(target).Length == 0)
        {
            TryDelete(target);
            throw new InvalidOperationException(
                $"ffmpeg produced nothing for '{Path.GetFileName(sourcePath)}'.");
        }

        return new CanonicalFile(target, await HashAsync(target, ct), new FileInfo(target).Length);
    }

    /// <summary>The containers whose index can be moved to the front, and whose muxer can be replaced by
    /// the generic mp4 one when the specific muxer rejects a codec.</summary>
    private static bool IsMp4Family(string extension) =>
        extension is ".m4a" or ".mp4" or ".m4b";

    /// <summary>The muxer does not accept this codec in this container. This is different from a source
    /// that ffmpeg cannot read at all. No other muxer would fix that.</summary>
    private static bool RefusedTheCodec(string stderr) =>
        stderr.Contains("Could not find tag for codec", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("Could not write header", StringComparison.OrdinalIgnoreCase);

    /// <summary>Runs one attempt at canonicalising. Returns null if it worked, or ffmpeg's error output if
    /// it did not.</summary>
    private static async Task<string?> RunAsync(
        string sourcePath, string target, string ext, bool forceMp4, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(FfmpegPath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(sourcePath);
        psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("0:a");   // audio only: no cover-art video stream
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("copy");    // never re-encode
        psi.ArgumentList.Add("-map_metadata"); psi.ArgumentList.Add("-1");
        psi.ArgumentList.Add("-map_chapters"); psi.ArgumentList.Add("-1");
        psi.ArgumentList.Add("-bitexact");                           // no encoder string, no timestamps

        // An mp4 written the normal way puts its index at the end. A player that streams it over HTTP must
        // fetch the end of the file before it can start. Moving the index to the front costs one extra pass
        // here, and saves a round trip on every play.
        if (IsMp4Family(ext))
        {
            psi.ArgumentList.Add("-movflags");
            psi.ArgumentList.Add("+faststart");
        }

        if (forceMp4)
        {
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("mp4");
        }

        psi.ArgumentList.Add(target);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("ffmpeg would not start.");

        var stderr = proc.StandardError.ReadToEndAsync(ct);
        _ = proc.StandardOutput.ReadToEndAsync(ct);   // drain so a full pipe cannot stall ffmpeg
        await proc.WaitForExitAsync(ct);

        return proc.ExitCode == 0 ? null : await stderr;
    }

    /// <summary>Lower-case hex SHA-256 of a file, streamed. A lossless album track can be tens of
    /// MB.</summary>
    public static async Task<string> HashAsync(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>The answer to one proof-of-possession challenge: SHA-256 over the server's nonce, followed
    /// by the exact bytes of <paramref name="path"/> in <paramref name="ranges"/>. Only someone who holds
    /// the file can produce it. Knowing its hash is not enough. This is what stops dedup from turning the
    /// store into a way to fetch other people's music.
    /// </summary>
    public static async Task<IReadOnlyList<string>> AnswerChallengeAsync(
        string path,
        byte[] nonce,
        IReadOnlyList<(long Offset, int Length)> ranges,
        CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        var answers = new List<string>(ranges.Count);

        foreach (var (offset, length) in ranges)
        {
            if (offset < 0 || length <= 0 || offset + length > stream.Length)
                throw new ArgumentOutOfRangeException(
                    nameof(ranges), $"Range {offset}+{length} is outside a {stream.Length}-byte file.");

            var buffer = new byte[length];
            stream.Seek(offset, SeekOrigin.Begin);
            await stream.ReadExactlyAsync(buffer, ct);

            using var sha = SHA256.Create();
            sha.TransformBlock(nonce, 0, nonce.Length, null, 0);
            sha.TransformFinalBlock(buffer, 0, buffer.Length);
            answers.Add(Convert.ToHexStringLower(sha.Hash!));
        }

        return answers;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}

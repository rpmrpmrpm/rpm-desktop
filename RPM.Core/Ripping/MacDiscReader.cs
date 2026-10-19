using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace RPM.Core.Ripping;

/// <summary>
/// Reads audio CDs on macOS. The kernel does most of the work. cddafs mounts an audio CD as a volume of
/// synthesized AIFF files, one per audio track, and puts the raw table of contents beside them in a
/// hidden <c>.TOC.plist</c>. So we parse that plist for the TOC and read the AIFF files for the audio.
/// No IOKit, no elevated privileges, and no GPL dependency.
///
/// cddafs does a plain read. It has no overlapped re-reads and no C2 error pointers. A clean disc rips
/// bit-perfect. A scratched disc gives interpolated samples with no error raised.
/// <see cref="CdRipper"/>'s verify pass (read every track twice and compare) is what covers that.
/// </summary>
public sealed class MacDiscReader : IDiscReader
{
    /// <summary>cddafs writes this file at the root of every mounted audio CD. Its presence is the cheapest
    /// reliable test for "this volume is an audio CD".</summary>
    private const string TocPlistName = ".TOC.plist";

    /// <summary>Where the kernel mounts removable volumes.</summary>
    private const string VolumesRoot = "/Volumes";

    /// <summary>False: cddafs writes AIFF-C with the 'sowt' compression type, which means byte-swapped, so
    /// the PCM is little-endian. <see cref="ReadTrackPcmAsync"/> converts the big-endian case to match, so
    /// what this reader returns is always little-endian.</summary>
    public bool PcmIsBigEndian => false;

    public async Task<IReadOnlyList<DiscDrive>> ListDrivesAsync(CancellationToken ct = default)
    {
        // `drutil list` prints a header row, then one row per drive. With no drive it prints only the header.
        // Each row starts with drutil's own 1-based drive index, which is the handle every other drutil call
        // takes. Then come vendor, product, revision, bus and support level:
        //
        //    Vendor   Product           Rev   Bus       SupportLevel
        // 1  MATSHITA DVD-R   UJ-898    HD10  USB       Apple Shipping
        var output = await RunAsync("drutil", ["list"], ct);
        var drives = new List<DiscDrive>();

        var rows = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1) // header
            .Where(line => line.Trim().Length > 0);

        foreach (var row in rows)
        {
            var columns = row.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (columns.Length < 3 ||
                !int.TryParse(columns[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                continue;

            // Vendor and product columns. The revision and bus are not needed in a drive picker.
            var name = string.Join(' ', columns.Skip(1).Take(3));
            drives.Add(new DiscDrive(index.ToString(CultureInfo.InvariantCulture), name));
        }

        return drives;
    }

    public async Task<DiscInfo?> ReadDiscAsync(DiscDrive drive, CancellationToken ct = default)
    {
        var mount = FindAudioCdMount();
        if (mount is null)
            return null;

        var toc = await ReadTocAsync(mount, ct);
        if (toc is null || !toc.AudioTracks.Any())
            return null;

        return new DiscInfo(drive, toc, MusicBrainzDiscId.Compute(toc), mount);
    }

    public async Task ReadTrackPcmAsync(DiscInfo disc, DiscTrack track, Stream destination,
        IProgress<double>? progress = null, CancellationToken ct = default, RipReadLog? log = null)
    {
        if (disc.MountPath is null)
            throw new DiscReadException("the audio CD is not mounted");

        var aiff = FindTrackFile(disc.MountPath, track.Number)
            ?? throw new DiscReadException($"no audio file on the disc for track {track.Number}");

        // No log means no tolerance. The first block that will not read fails the track. A caller with
        // nothing to report damage to should get this.
        log ??= new RipReadLog(new RipReadPolicy { Retries = 0, MaxBadBlocks = 0 });

        var file = File.OpenRead(aiff);

        // Set by any exit other than a clean read to the end of the track. Every such exit (a skip, a
        // cancel, or a stuck drive) can leave an abandoned read still holding this handle. A read that this
        // file cannot answer is one that its close cannot answer either.
        var abandoned = false;
        try
        {
            var audio = ReadAiffDataChunk(file, aiff);

            var buffer = new byte[64 * 1024];
            long copied = 0;

            while (copied < audio.Length)
            {
                ct.ThrowIfCancellationRequested();
                var want = (int)Math.Min(buffer.Length, audio.Length - copied);
                var at = audio.Offset + copied;

                // A stall or a skip leaves the read running. It still owns this handle and this buffer. So the
                // track ends here, rather than reading on into bytes that something else may be writing.
                var read = await DiscBlockReader.ReadAsync(
                    async token =>
                    {
                        // Set again on every attempt. A refused read leaves the position wherever the kernel left it,
                        // and a retry must ask for the same bytes.
                        file.Position = at;
                        return await file.ReadAsync(buffer.AsMemory(0, want), token);
                    },
                    log, $"bytes {at}..{at + want}", ct);

                if (read == DiscBlockReader.Unreadable)
                {
                    // Silence goes in its place. Everything after the damage stays in the right place in the track,
                    // instead of sliding forward by the missing block.
                    await WriteSilenceAsync(destination, want, ct);
                    copied += want;
                    progress?.Report((double)copied / audio.Length);
                    continue;
                }

                if (read == 0)
                    break; // A short file means a read error that the kernel hid. Encode what we got.

                // Everything we emit is little-endian. A big-endian AIFF is swapped here, rather than passing
                // that difference on to the encoder.
                if (audio.IsBigEndian)
                    SwapSampleBytes(buffer.AsSpan(0, read));

                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                copied += read;
                progress?.Report((double)copied / audio.Length);
            }

            progress?.Report(1);
        }
        catch
        {
            abandoned = true;
            throw;
        }
        finally
        {
            // Closing a handle that an abandoned read is still using can block for as long as the read runs.
            // So any exit other than a clean finish disposes the handle where nobody is waiting for it.
            if (abandoned)
                _ = Task.Run(() => { try { file.Dispose(); } catch { } });
            else
                await file.DisposeAsync();
        }
    }

    /// <summary>Digital silence, in place of samples the drive would not give up.</summary>
    private static async Task WriteSilenceAsync(Stream destination, int bytes, CancellationToken ct)
    {
        var silence = new byte[Math.Min(bytes, 64 * 1024)];
        for (var written = 0; written < bytes;)
        {
            var chunk = Math.Min(silence.Length, bytes - written);
            await destination.WriteAsync(silence.AsMemory(0, chunk), ct);
            written += chunk;
        }
    }

    public async Task EjectAsync(DiscDrive drive, CancellationToken ct = default)
    {
        try { await RunAsync("drutil", ["-drive", drive.Id, "tray", "eject"], ct); }
        catch { /* Ejecting is a courtesy. Never fail a finished rip because of it. */ }
    }

    /// <summary>An audio CD is in the drive when cddafs has one mounted. This is the same test that
    /// <see cref="FindAudioCdMount"/> makes. It costs no more than a couple of stat() calls.</summary>
    public bool AudioDiscPresent() => FindAudioCdMount() is not null;

    /// <summary>The mounted audio CD, found by its <c>.TOC.plist</c> and not by volume name. Finder and
    /// Music.app rename the volume as soon as they recognise the disc, so "/Volumes/Audio CD" is not a
    /// path you can rely on.</summary>
    /// <param name="volumesRoot">Can be overridden, so the test suite can point this at a folder it built.
    /// A test run cannot mount a CD.</param>
    public static string? FindAudioCdMount(string volumesRoot = VolumesRoot)
    {
        try
        {
            return Directory.EnumerateDirectories(volumesRoot)
                .FirstOrDefault(dir => File.Exists(Path.Combine(dir, TocPlistName)));
        }
        catch
        {
            return null;
        }
    }


    /// <summary>Parses the TOC out of the volume's <c>.TOC.plist</c>. It uses plutil, so no plist parser
    /// dependency is needed.
    ///
    /// It extracts only the Sessions key, not the whole file. The plist also holds the raw TOC as a binary
    /// blob ("Format 0x02 TOC Data"), and plutil refuses to convert any plist that contains binary data to
    /// JSON. Converting the whole file would simply fail.</summary>
    private async Task<DiscToc?> ReadTocAsync(string mount, CancellationToken ct)
    {
        var plistPath = Path.Combine(mount, TocPlistName);
        string json;
        try
        {
            json = await RunAsync("plutil", ["-extract", "Sessions", "json", "-o", "-", plistPath], ct);
        }
        catch (Exception ex)
        {
            throw new DiscReadException($"could not read the disc's table of contents: {ex.Message}", ex);
        }

        using var doc = JsonDocument.Parse(json);
        var sessions = doc.RootElement;
        if (sessions.ValueKind != JsonValueKind.Array || sessions.GetArrayLength() == 0)
            return null;

        // A hybrid disc has a second, data session. The audio always lives in the first.
        var session = sessions[0];

        var firstTrack = GetInt(session, 1, "First Track");
        var lastTrack = GetInt(session, 0, "Last Track");
        var leadout = GetInt(session, 0, "Leadout Block");

        if (!session.TryGetProperty("Track Array", out var trackArray) ||
            trackArray.ValueKind != JsonValueKind.Array)
            return null;

        var tracks = new List<DiscTrack>();
        foreach (var entry in trackArray.EnumerateArray())
        {
            var point = GetInt(entry, 0, "Point");
            if (point is < 1 or > 99) // 0xA0 to 0xA2 are lead-in and lead-out descriptors, not real tracks
                continue;

            // cddafs reports absolute frame addresses. Track 1 of a normal disc starts at block 150, not 0, so
            // the lead-in is already in these numbers. The rest of the code works in LBA, which leaves it out.
            var lba = StartBlockToLba(GetInt(entry, 0, "Start Block"));
            var isData = entry.TryGetProperty("Data", out var data) && data.ValueKind == JsonValueKind.True;

            tracks.Add(new DiscTrack(point, lba, isData));
        }

        if (tracks.Count == 0)
            return null;

        tracks.Sort((a, b) => a.Number.CompareTo(b.Number));
        if (lastTrack == 0)
            lastTrack = tracks[^1].Number;

        return new DiscToc(firstTrack, lastTrack, tracks, StartBlockToLba(leadout));
    }

    /// <summary>cddafs block addresses include the 150-frame lead-in. An LBA does not.</summary>
    public static int StartBlockToLba(int startBlock) =>
        Math.Max(0, startBlock - MusicBrainzDiscId.LeadIn);

    /// <summary>cddafs names its files "1 Audio Track.aiff", so the leading number is the track number. It is
    /// parsed, not rebuilt, because the exact wording is not a contract.</summary>
    private static int? TrackNumberOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var digits = new string(name.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private static string? FindTrackFile(string mount, int trackNumber)
    {
        try
        {
            return Directory.EnumerateFiles(mount, "*.aiff")
                .FirstOrDefault(file => TrackNumberOf(file) == trackNumber);
        }
        catch { return null; }
    }

    /// <summary>Where a track's samples start, how many bytes they run, and which way round they are.</summary>
    private readonly record struct AiffAudio(long Offset, long Length, bool IsBigEndian);

    /// <summary>
    /// Walks the chunk list to the SSND chunk. The container's own fields are always big-endian, but the
    /// SAMPLES may not be. cddafs writes AIFF-C (form type "AIFC") whose COMM chunk has the compression
    /// type <c>sowt</c>. That is "twos" backwards, and it means byte-swapped, so the PCM is little-endian.
    /// Assuming plain big-endian AIFF here would turn a CD into white noise.
    /// </summary>
    private static AiffAudio ReadAiffDataChunk(Stream stream, string path)
    {
        Span<byte> header = stackalloc byte[12];
        stream.ReadExactly(header);

        var isAiff = header[8..12].SequenceEqual("AIFF"u8);
        var isAifc = header[8..12].SequenceEqual("AIFC"u8);
        if (!header[..4].SequenceEqual("FORM"u8) || (!isAiff && !isAifc))
            throw new DiscReadException($"'{Path.GetFileName(path)}' is not an AIFF file");

        // Uncompressed AIFF is big-endian. AIFF-C says so in its COMM chunk. Until that chunk is read, the
        // safe assumption is the format's own default.
        var bigEndian = true;

        // One buffer each, allocated before the loop. A stackalloc inside the loop would grow the frame on
        // every chunk (CA2014).
        Span<byte> chunkHeader = stackalloc byte[8];
        Span<byte> comm = stackalloc byte[22];
        Span<byte> ssnd = stackalloc byte[8];
        while (stream.Position < stream.Length)
        {
            if (stream.Read(chunkHeader) < 8)
                break;

            var id = chunkHeader[..4].ToArray();
            var size = ReadBigEndianInt32(chunkHeader[4..8]);
            var next = stream.Position + size + (size % 2); // chunks are word-aligned

            if (id.AsSpan().SequenceEqual("COMM"u8) && isAifc && size >= 22)
            {
                // AIFF-C COMM: channels(2) frames(4) sampleSize(2) sampleRate(10) compressionType(4)
                stream.ReadExactly(comm);

                var compression = comm[18..22];
                if (compression.SequenceEqual("sowt"u8))
                    bigEndian = false;                       // byte-swapped: little-endian
                else if (compression.SequenceEqual("NONE"u8) || compression.SequenceEqual("twos"u8))
                    bigEndian = true;
                else
                    throw new DiscReadException(
                        $"'{Path.GetFileName(path)}' is compressed ({System.Text.Encoding.ASCII.GetString(compression)}), " +
                        "which this ripper cannot read");
            }
            else if (id.AsSpan().SequenceEqual("SSND"u8))
            {
                // SSND leads with an 8-byte offset/blockSize pair before the samples themselves.
                stream.ReadExactly(ssnd);
                var dataOffset = ReadBigEndianInt32(ssnd[..4]);
                var start = stream.Position + dataOffset;
                var length = size - 8 - dataOffset;
                return new AiffAudio(start, Math.Max(0, length), bigEndian);
            }

            stream.Position = next;
        }

        throw new DiscReadException($"'{Path.GetFileName(path)}' has no audio data");
    }

    /// <summary>Flips each 16-bit sample end for end, in place.</summary>
    private static void SwapSampleBytes(Span<byte> samples)
    {
        for (var i = 0; i + 1 < samples.Length; i += 2)
            (samples[i], samples[i + 1]) = (samples[i + 1], samples[i]);
    }

    private static int ReadBigEndianInt32(ReadOnlySpan<byte> bytes) =>
        (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];

    private static bool TryGetProperty(JsonElement element, out JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out value))
                return true;
        }
        value = default;
        return false;
    }

    private static int GetInt(JsonElement element, int fallback, params string[] names) =>
        TryGetProperty(element, out var value, names) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : fallback;

    private static async Task<string> RunAsync(string command, string[] arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi)
            ?? throw new DiscReadException($"could not run {command}");

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            throw new DiscReadException($"{command} failed: {stderr.Trim()}");

        return stdout;
    }
}

namespace RPM.Core.Ripping;

/// <summary>
/// A title set's VOB files, addressed as one continuous run of 2048-byte sectors. This is how the IFO's
/// cell table refers to them.
///
/// A DVD splits a title across VTS_nn_1.VOB, VTS_nn_2.VOB and so on. It does this only because the file
/// system caps a file at 1 GB. Logically they are one stream. A cell's sector numbers count from the start
/// of the first file, straight through the rest. So a cell can begin in one file and end in the next. This
/// class joins them back together.
/// </summary>
public sealed class VobSectorStream : IAsyncDisposable
{
    /// <summary>A DVD sector. Every VOB is a whole number of these.</summary>
    public const int SectorSize = 2048;

    private readonly List<FileStream> _files = [];
    private readonly List<int> _startSector = [];   // first global sector of each file
    private readonly int _totalSectors;
    private readonly CssDecryptor? _css;

    /// <param name="css">When set, the VOBs are CSS-scrambled. Every sector is then read through libdvdcss from
    /// the raw device, not straight from the mounted file, which is still scrambled. The mounted files stay
    /// open in any case, because <c>F_LOG2PHYS_EXT</c> uses them to find the device sector.</param>
    public VobSectorStream(IReadOnlyList<string> vobPaths, CssDecryptor? css = null)
    {
        _css = css;

        var sector = 0;
        foreach (var path in vobPaths)
        {
            var file = File.OpenRead(path);
            _files.Add(file);
            _startSector.Add(sector);
            sector += (int)(file.Length / SectorSize);
        }
        _totalSectors = sector;
    }

    /// <summary>Copies the sectors of the given cells, in order, into <paramref name="destination"/>.
    /// <paramref name="progress"/> runs 0..1 across all the cells together.</summary>
    /// <exception cref="DvdEncryptedException">The disc turned out to be scrambled and nothing is descrambling
    /// it. The rip stops and says so. It does not pass the bytes to the decoder, which would turn them into
    /// white noise.
    /// </exception>
    /// <param name="log">Where sectors the disc would not give up are reported, and the policy that says how
    /// hard to try for them. A run that is written off goes out as silence, so the chapter keeps its timing.
    /// A chapter past its budget throws <see cref="DiscTrackUnreadableException"/>, and the ripper skips it.
    /// Null means the first refused read fails the chapter.</param>
    public async Task CopySectorsAsync(IReadOnlyList<DvdCellRange> cells, Stream destination,
        IProgress<double>? progress = null, CancellationToken ct = default, RipReadLog? log = null)
    {
        var total = cells.Sum(cell => (long)cell.SectorCount);
        if (total <= 0)
            throw new DiscReadException("this chapter occupies no sectors on the disc");

        log ??= new RipReadLog(new RipReadPolicy { Retries = 0, MaxBadBlocks = 0 });

        // Bigger reads make the raw device round trip cheaper, which matters for CSS descrambling. A clear
        // disc reads through the OS cache, so a modest buffer is enough there.
        var buffer = new byte[(_css is null ? 64 : 512) * SectorSize];
        long copied = 0;

        foreach (var cell in cells)
        {
            var sector = cell.FirstSector;
            var remaining = cell.SectorCount;

            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();

                var chunk = Math.Min(remaining, buffer.Length / SectorSize);
                var at = sector;

                // Retried, and given only so long to answer. A worn DVD returns read errors, and a failing drive
                // returns nothing at all. Either way the chapter must not hang on it.
                var read = await DiscBlockReader.ReadAsync(
                    token => ReadSectorsAsync(at, chunk, buffer, token),
                    log, $"sectors {at}..{at + chunk - 1}", ct);

                if (read == DiscBlockReader.Unreadable)
                {
                    // Silence goes in its place. The demuxer then sees a gap, not the next cell's audio moved forward
                    // into it. The difference is between a click and a mess.
                    Array.Clear(buffer, 0, chunk * SectorSize);
                    await destination.WriteAsync(buffer.AsMemory(0, chunk * SectorSize), ct);

                    sector += chunk;
                    remaining -= chunk;
                    copied += chunk;
                    progress?.Report(Math.Clamp((double)copied / total, 0, 1));
                    continue;
                }

                if (read == 0)
                    break; // Ran off the end of the disc. Encode what we have rather than fail.

                GuardAgainstScrambledBytes(buffer, read);

                await destination.WriteAsync(buffer.AsMemory(0, read * SectorSize), ct);

                sector += read;
                remaining -= read;
                copied += read;
                progress?.Report(Math.Clamp((double)copied / total, 0, 1));
            }
        }

        progress?.Report(1);
    }

    /// <summary>
    /// The last check against silently ripping a protected disc into noise.
    ///
    /// <para>
    /// CSS leaves every header in the clear and scrambles only the payload. So scrambled sectors demux
    /// perfectly. ffmpeg finds the audio packets, decodes them, exits with code 0, and writes a file full of
    /// confident white noise. Nothing downstream reports an error. This is also how a disc that scrambles
    /// only some of its packs got past a detector that sampled just a few of them. So the bytes are checked
    /// as they pass, where the truth is. A scrambled media pack that reaches an un-descrambled read is not a
    /// rip, and it stops here.
    /// </para>
    /// </summary>
    private void GuardAgainstScrambledBytes(byte[] buffer, int sectors)
    {
        if (_css is not null)
            return;   // libdvdcss is descrambling, so scrambled packs are expected here.

        for (var i = 0; i < sectors; i++)
        {
            if (ProgramStream.IsScrambled(buffer.AsSpan(i * SectorSize, SectorSize)))
                throw new DvdEncryptedException(
                    "this DVD is copy-protected (CSS) and cannot be read without descrambling: "
                    + CssDecryptor.InstallHint);
        }
    }

    /// <summary>Reads up to <paramref name="count"/> sectors, starting at a global sector number. The run can
    /// cross a VOB boundary. Returns how many sectors were actually read.</summary>
    private async Task<int> ReadSectorsAsync(int sector, int count, byte[] buffer, CancellationToken ct)
    {
        if (sector < 0 || sector >= _totalSectors)
            return 0;

        var got = 0;
        while (got < count && sector + got < _totalSectors)
        {
            var index = FileIndexOf(sector + got);
            if (index < 0)
                break;

            var file = _files[index];
            var fileSectors = (int)(file.Length / SectorSize);
            var sectorInFile = sector + got - _startSector[index];

            // Take as much as this file can give, before the run continues in the next one.
            var take = Math.Min(count - got, fileSectors - sectorInFile);
            if (take <= 0)
                break;

            if (_css is not null)
            {
                // Scrambled: the byte offset inside this file maps to a physical device sector, which libdvdcss
                // reads back decrypted. Never read past a contiguous run in one call.
                var (deviceSector, contig) = CssDecryptor.Locate(file.SafeFileHandle, (long)sectorInFile * SectorSize);
                take = Math.Min(take, contig);
                var decrypted = _css.ReadDecrypted(deviceSector, take, buffer, got * SectorSize);
                if (decrypted <= 0)
                    break;
                take = decrypted;
            }
            else
            {
                file.Position = (long)sectorInFile * SectorSize;
                await file.ReadExactlyAsync(buffer.AsMemory(got * SectorSize, take * SectorSize), ct);
            }

            got += take;
        }

        return got;
    }

    /// <summary>Which VOB file a global sector lives in.</summary>
    private int FileIndexOf(int sector)
    {
        for (var i = _files.Count - 1; i >= 0; i--)
        {
            if (sector >= _startSector[i])
                return i;
        }
        return -1;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var file in _files)
            await file.DisposeAsync();
        _files.Clear();
    }
}

using RPM.Core.Art;

namespace RPM.Core.Ripping;

/// <summary>A run of sectors on the disc that holds one cell. A cell is the physical unit that a DVD
/// addresses content in. Sector numbers are relative to the start of the title set's VOB stream. A
/// sector is 2048 bytes.</summary>
public sealed record DvdCellRange(int FirstSector, int LastSector)
{
    /// <summary>Includes both ends. A one-sector cell has first == last.</summary>
    public int SectorCount => Math.Max(0, LastSector - FirstSector + 1);
}

/// <summary>
/// One chapter of a DVD title. On a concert DVD a chapter is almost always a song. That is why RPM
/// turns chapters, not titles, into tracks.
///
/// <para><see cref="Cells"/> is what the ripper actually cuts on. A DVD's timeline is NOT in the VOB
/// timestamps. Those restart from zero at each cell, so seeking to a time lands in the wrong place.
/// The IFO's cell table is the only real map, and it is in sectors.</para>
/// </summary>
/// <param name="Number">1-based chapter number within its title.</param>
/// <param name="Start">Where the chapter begins, measured from the start of the title.</param>
public sealed record DvdChapter(
    int Number, TimeSpan Start, TimeSpan Duration, IReadOnlyList<DvdCellRange> Cells);

/// <summary>One audio track of a DVD title. A concert disc often has a stereo mix and a surround
/// mix, and the user picks one.</summary>
/// <param name="Index">The audio stream's ordinal, which is ffmpeg's <c>0:a:N</c>.</param>
public sealed record DvdAudioStream(int Index, string Codec, int Channels, string? Language)
{
    /// <summary>What the picker shows, for example "AC-3 5.1 (English)".</summary>
    public string DisplayName
    {
        get
        {
            var layout = Channels switch
            {
                1 => "mono",
                2 => "stereo",
                6 => "5.1",
                8 => "7.1",
                _ => $"{Channels}ch",
            };
            var name = $"{Codec.ToUpperInvariant()} {layout}";
            return string.IsNullOrWhiteSpace(Language) ? name : $"{name} ({Language})";
        }
    }
}

/// <summary>A playable title on a DVD: one program chain (PGC) of one title set, plus the VOB files
/// that its audio is stored in.</summary>
/// <param name="Scrambled">True when this title's VOBs are CSS-encrypted. The audio cannot be read
/// without libdvdcss to descramble it from the raw device.</param>
public sealed record DvdTitle(
    int Number,
    TimeSpan Duration,
    IReadOnlyList<DvdChapter> Chapters,
    IReadOnlyList<string> VobPaths,
    IReadOnlyList<DvdAudioStream> AudioStreams,
    bool Scrambled);

/// <summary>A video DVD in a drive, with its titles sorted longest first. The main feature is what a
/// user usually wants to rip, and on a concert disc it is much longer than the menus and extras.</summary>
/// <param name="RawDevice">The raw optical device that libdvdcss needs to descramble a CSS title:
/// <c>/dev/rdiskN</c> on macOS, or a drive spec (<c>D:</c>) on Windows. Null when it could not be
/// resolved. In that case only unscrambled titles can be ripped.</param>
public sealed record DvdInfo(
    DiscDrive Drive, string MountPath, string? VolumeName, IReadOnlyList<DvdTitle> Titles, string? RawDevice);

/// <summary>Everything the DVD ripper needs: which title, which audio stream, and the chapters the
/// user kept, each already named. It also has the sleeve. A DVD has no cover of its own, so the sleeve
/// the user picked in the lookup is the only art the rip gets. Its two sizes go to two places: the full
/// scan beside the tracks as the folder's cover image, and the scaled copy inside them.</summary>
/// <param name="DiscArt">A transparent PNG of the disc itself, written to the album folder as
/// disc.png. A CD rip carries the same picture. A music DVD has a printed label like any other disc,
/// so it is included too.</param>
public sealed record DvdRipPlan(
    DvdInfo Disc,
    DvdTitle Title,
    DvdAudioStream Audio,
    IReadOnlyList<RipTrackPlan> Tracks,
    RipFormat Format,
    Sleeve? Cover = null,
    byte[]? DiscArt = null);

/// <summary>Thrown when the DVD is readable but scrambled. This is a commercial disc with CSS on it,
/// and no libdvdcss is installed to undo it.</summary>
public sealed class DvdEncryptedException : Exception
{
    public DvdEncryptedException(string message) : base(message) { }
}

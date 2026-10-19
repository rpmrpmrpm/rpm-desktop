#if DEBUG
using RPM.Core.Net;
using RPM.Core.Metadata.Providers;
using RPM.Core.Ripping;
using RPM.Fingerprint.Catalog;
using RPM.Desktop.Services;

namespace RPM.Desktop.Presentation.Gallery;

/// <summary> A <see cref="RipperService"/> with a disc permanently in the drive. The CD and DVD rip
/// screens otherwise need a physical disc before they can appear at all. With this, they can be opened
/// and driven in the gallery. It hands back a made-up audio CD (a full table of contents, named by a
/// fake catalogue lookup) and a made-up music DVD (titles, chapters and an audio stream). It refuses to
/// rip for real. The point is to <i>see</i> the screens, and a real rip needs hardware that is not
/// there. </summary>
internal sealed class GalleryRipperService(IRpmMetadataApi catalog) : RipperService(catalog)
{
    private static readonly DiscDrive Drive = new("gallery-optical-0", "Gallery Optical Drive");

    public override bool IsSupported => true;

    public override bool DvdPresent() => true;

    /// <summary>A disc is always in the gallery's drive. The watcher offers to rip it once, as it does
    /// when a real disc goes in. Without this, the deck's rip question could not be reached without
    /// hardware.</summary>
    public override bool CdPresent() => true;

    public override Task<IReadOnlyList<DiscDrive>> ListDrivesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DiscDrive>>([Drive]);

    public override Task<DiscInfo?> ReadDiscAsync(DiscDrive drive, CancellationToken ct = default) =>
        Task.FromResult<DiscInfo?>(BuildCd());

    public override Task<DvdInfo?> ReadDvdAsync(DiscDrive drive, CancellationToken ct = default) =>
        Task.FromResult<DvdInfo?>(BuildDvd());

    public override Task EjectAsync(DiscDrive drive, CancellationToken ct = default) => Task.CompletedTask;

    // Naming: hand back a fake album for the CD, so its rip list reads like a real disc. A DVD is named
    // through the shared release lookup, which the demo leaves to the user.
    public override Task<Asked<CatalogDisc?>> LookupDiscAsync(DiscInfo disc, CancellationToken ct = default) =>
        Task.FromResult(Asked.Answer<CatalogDisc?>(CdLookup()));

    // Ripping needs the drive that is not there, so refuse clearly rather than touch hardware.
    public override Task<RipResult> RipAsync(RipPlan plan, string stagingRoot, bool verify,
        RPM.Core.Import.NamingTemplate layout, IProgress<RipProgress>? progress = null, CancellationToken ct = default,
        RipHooks? hooks = null) =>
        throw new InvalidOperationException("Ripping is disabled in the gallery — this is a demo disc.");

    public override Task<RipResult> RipDvdAsync(DvdRipPlan plan, string stagingRoot,
        RPM.Core.Import.NamingTemplate layout, IProgress<RipProgress>? progress = null, CancellationToken ct = default,
        RipHooks? hooks = null) =>
        throw new InvalidOperationException("Ripping is disabled in the gallery — this is a demo disc.");

    // ── fake CD ──

    private static readonly (string Title, int Seconds)[] CdTracks =
    [
        ("Coastline", 214), ("Paper Boats", 191), ("Slow Ferry", 246), ("Harbour Lights", 168),
        ("Undertow", 233), ("Salt Air", 205), ("The Long Way Back", 278), ("Driftwood", 159),
        ("Low Tide", 224), ("Homeward", 262),
    ];

    private static DiscInfo BuildCd()
    {
        var tracks = new List<DiscTrack>();
        var lba = 0;
        foreach (var (_, seconds) in CdTracks)
        {
            tracks.Add(new DiscTrack(tracks.Count + 1, lba, IsData: false));
            lba += seconds * DiscToc.FramesPerSecond;   // 75 frames per second
        }
        var toc = new DiscToc(1, tracks.Count, tracks, lba);
        return new DiscInfo(Drive, toc, DiscId: "gallery-demo-cd", MountPath: null);
    }

    private static CatalogDisc CdLookup()
    {
        var tracks = CdTracks
            .Select((t, i) => new CatalogTrack((uint)(i + 1), t.Title, "The Gallery Sessions", t.Seconds))
            .ToList();
        return new CatalogDisc(
            ReleaseId: "gallery-demo-release", Title: "Night Cassette", Artist: "The Gallery Sessions",
            Year: 2019, DiscNumber: 1, DiscCount: 1, Tracks: tracks);
    }

    // ── fake DVD ──

    private static readonly (string Name, int Seconds)[] DvdChapters =
    [
        ("Intro", 168), ("Opening Number", 296), ("Ballad", 341), ("Guitar Solo", 214),
        ("Encore", 388), ("Credits", 122),
    ];

    private static DvdInfo BuildDvd()
    {
        var chapters = new List<DvdChapter>();
        var start = TimeSpan.Zero;
        foreach (var (_, seconds) in DvdChapters)
        {
            var duration = TimeSpan.FromSeconds(seconds);
            chapters.Add(new DvdChapter(chapters.Count + 1, start, duration, Cells: []));
            start += duration;
        }
        var title = new DvdTitle(
            Number: 1, Duration: start, Chapters: chapters, VobPaths: [],
            AudioStreams: [new DvdAudioStream(0, "AC-3", 6, "English")],
            Scrambled: false);   // Unencrypted. Otherwise the dialog stops at the libdvdcss gate.
        return new DvdInfo(Drive, MountPath: "/Volumes/GALLERY_LIVE", VolumeName: "GALLERY LIVE",
            Titles: [title], RawDevice: null);
    }
}
#endif

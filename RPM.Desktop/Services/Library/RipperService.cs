using RPM.Core.Net;
using RPM.Core.Metadata.Providers;
using RPM.Core.Import;
using RPM.Core.Ripping;
using RPM.Fingerprint.Catalog;

namespace RPM.Desktop.Services.Library;

/// <summary>
/// The app's way in to CD ripping. It picks the right <see cref="IDiscReader"/> for the platform,
/// keeps the disc work off the UI thread, and names discs through the rpm catalogue.
///
/// Every drive and disc call blocks on hardware, and a spin-up can take seconds, so all of them run on
/// the thread pool. On a platform with no reader the drive list is empty, and the UI offers only the
/// folder path.
/// </summary>
public class RipperService
{
    private readonly IDiscReader? _reader;
    private readonly IRpmMetadataApi _catalog;

    public RipperService(IRpmMetadataApi catalog)
    {
        _catalog = catalog;
        _reader = OperatingSystem.IsMacOS() ? new MacDiscReader()
            : OperatingSystem.IsWindows() ? new WindowsDiscReader()
            : null;
    }

    /// <summary>False where we have no way to talk to an optical drive at all.</summary>
    public virtual bool IsSupported => _reader is not null;

    public virtual async Task<IReadOnlyList<DiscDrive>> ListDrivesAsync(CancellationToken ct = default)
    {
        if (_reader is null)
            return [];

        try { return await Task.Run(() => _reader.ListDrivesAsync(ct)); }
        catch { return []; } // No drive is a normal state, so it is not reported as an error.
    }

    public virtual Task<DiscInfo?> ReadDiscAsync(DiscDrive drive, CancellationToken ct = default) =>
        _reader is null
            ? Task.FromResult<DiscInfo?>(null)
            : Task.Run(() => _reader.ReadDiscAsync(drive, ct));

    /// <summary>True when an audio CD is in a drive right now. The check is cheap on purpose: it reads
    /// what the OS has already mounted and never touches the drive. The disc watcher calls it every few
    /// seconds while the app runs.</summary>
    public virtual bool CdPresent() => _reader?.AudioDiscPresent() ?? false;

    /// <summary>Names the disc through the rpm catalogue. If the exact disc ID is unknown, it falls back
    /// to a fuzzy search on the table of contents. Returns null when no catalogue knows the disc, and an
    /// unanswered result when the catalogue did not reply.</summary>
    public virtual Task<Asked<CatalogDisc?>> LookupDiscAsync(DiscInfo disc, CancellationToken ct = default) =>
        _catalog.LookupDiscAsync(disc.DiscId, MusicBrainzDiscId.TocString(disc.Toc), ct);

    public virtual Task<RipResult> RipAsync(RipPlan plan, string stagingRoot, bool verify,
        NamingTemplate layout, IProgress<RipProgress>? progress = null, CancellationToken ct = default,
        RipHooks? hooks = null)
    {
        if (_reader is null)
            throw new InvalidOperationException("ripping is not supported on this platform");

        var ripper = new CdRipper(_reader) { Verify = verify };
        return Task.Run(() => ripper.RipAsync(plan, stagingRoot, layout, progress, ct, hooks: hooks));
    }

    // ── video DVDs ───────────────────────────────────────────────────────────────────────────

    /// <summary>The video DVD in the drive, or null when there is none. Unlike a CD, a DVD is read
    /// through the filesystem that the OS has already mounted, so no drive handle is needed. The drive is
    /// passed only so the disc can be ejected afterwards.</summary>
    public virtual Task<DvdInfo?> ReadDvdAsync(DiscDrive drive, CancellationToken ct = default)
    {
        var mount = DvdReader.FindDvdVolumes().FirstOrDefault();
        return mount is null
            ? Task.FromResult<DvdInfo?>(null)
            : Task.Run(() => new DvdReader().ReadDvdAsync(drive, mount, ct));
    }

    /// <summary>True when a video DVD is sitting in a drive right now.</summary>
    public virtual bool DvdPresent() => DvdReader.FindDvdVolumes().Count > 0;

    public virtual Task<RipResult> RipDvdAsync(DvdRipPlan plan, string stagingRoot,
        NamingTemplate layout, IProgress<RipProgress>? progress = null, CancellationToken ct = default,
        RipHooks? hooks = null) =>
        Task.Run(() => new DvdRipper().RipAsync(plan, stagingRoot, layout, progress, ct, hooks));

    public virtual Task EjectAsync(DiscDrive drive, CancellationToken ct = default) =>
        _reader is null ? Task.CompletedTask : Task.Run(() => _reader.EjectAsync(drive, ct));
}

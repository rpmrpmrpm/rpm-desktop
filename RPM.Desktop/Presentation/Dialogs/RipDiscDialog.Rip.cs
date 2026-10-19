using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using RPM.Core.Analysis;
using RPM.Core.Art;
using RPM.Core.Metadata.Providers;
using RPM.Core.Models;
using RPM.Core.Ripping;
using RPM.Core.Splitting;
using RPM.Desktop.Services;
using RPM.Desktop.Strings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>
/// The rip itself: running it, previewing what came off, and steering a rip already in flight.
/// </summary>
public sealed partial class RipDiscDialog
{
    // ── The rip ──────────────────────────────────────────────────────────────────────────────

    private void StartRip()
    {
        var chosen = _tracks.Where(t => t.IsChecked).ToList();
        if (chosen.Count == 0)
            return;

        var format = (RipFormat)Math.Max(0, FormatCombo.SelectedIndex);
        var album = Blank(AlbumBox.Text);
        var artist = Blank(ArtistBox.Text);
        uint.TryParse(YearBox.Text, out var year);
        var country = Blank(CountryBox.Text);
        // What the picked pressing says about the record as a whole. Every track carries it, because tags
        // are per file. A rip is the only moment any of it is in hand.
        var release = _release;
        uint movements = (uint)chosen.Count(t => !string.IsNullOrWhiteSpace(t.Catalogue?.MovementName));

        // The MusicBrainz id tags hold MusicBrainz ids only. A Discogs release number is a valid id in a
        // different catalogue. Writing it into MUSICBRAINZ_ALBUMID would make every later lookup, ours
        // and every other tagger's, resolve to whatever release happens to share that number, or to
        // nothing at all.
        bool musicBrainz = string.Equals(
            release?.Ref.Provider, RpmMetadataProvider.ProviderId, StringComparison.OrdinalIgnoreCase);
        string? Mbid(string? value) => musicBrainz ? Blank(value) : null;

        // How many movements the work has. The listing says so where it groups them. A Discogs sonata is
        // one entry with its movements inside it. Where the listing does not group them, a disc whose
        // every track is a movement is the work itself. That is the usual case for a classical CD.
        uint MovementsOf(RipTrackRow row) =>
            row.Catalogue is { MovementCount: > 0 } listed ? listed.MovementCount
            : movements > 1 && movements == (uint)chosen.Count ? movements
            : 0;

        var trackPlans = chosen.Select(t => new RipTrackPlan(
            Number: t.Number,
            // The whole name, with the work included. The row shows the movement alone, because the work
            // is printed over the run it belongs to. A file has no such run.
            Title: t.FullTitle,
            // The track's own artist, when the listing gives one. Putting the album artist on every row
            // would rip a various-artists disc as forty tracks by one person.
            Artist: Blank(t.Catalogue?.Artist) ?? artist,
            Album: album,
            AlbumArtist: artist,
            Year: year,
            Genre: Blank(release?.Genre),
            Subgenre: Blank(release?.Subgenre),
            TrackCount: chosen.Count,
            Country: country ?? Blank(release?.Country),
            DiscNumber: (int)_discNumber,
            DiscCount: (int)_discCount,
            DiscSubtitle: _discSubtitle,
            Advisory: t.Catalogue?.Advisory ?? ContentAdvisory.Unknown,

            ArtistSort: Blank(t.Catalogue?.ArtistSort) ?? Blank(release?.ArtistSort),
            AlbumArtistSort: Blank(release?.ArtistSort),
            Composers: Blank(t.Catalogue?.Composers),
            ComposerSort: Blank(t.Catalogue?.ComposerSort),
            Conductor: Blank(t.Catalogue?.Conductor),
            Lyricist: Blank(t.Catalogue?.Lyricist),
            Arranger: Blank(t.Catalogue?.Arranger),
            Ensemble: Blank(t.Catalogue?.Ensemble),
            Soloists: Blank(t.Catalogue?.Soloists),

            // The row's own work and movement. This is what the user has in front of them: the heading
            // over the run, and the name they may have retyped in the box.
            Work: Blank(t.Work) ?? Blank(t.Catalogue?.Work),
            MovementName: t.Work is null ? Blank(t.Catalogue?.MovementName) : Blank(t.Title),
            MovementNumber: t.Catalogue?.MovementNumber ?? 0,
            MovementCount: MovementsOf(t),

            Isrc: Blank(t.Catalogue?.Isrc),
            Barcode: Blank(release?.Barcode),
            CatalogNumber: Blank(release?.CatalogNumber),
            Label: Blank(release?.Label),
            Media: Blank(release?.Format),
            OriginalYear: release?.OriginalYear ?? 0,

            RecordingId: Mbid(t.Catalogue?.RecordingId),
            ReleaseId: Mbid(release?.Ref.Id),
            ReleaseGroupId: Mbid(release?.ReleaseGroupId),
            ArtistId: Mbid(t.Catalogue?.ArtistId) ?? Mbid(release?.ArtistId),
            ReleaseArtistId: Mbid(release?.ArtistId))).ToList();

        // The job runs in the app, not in this window. The rip carries on if the window is closed.
        // Everything the rip face shows from here on is read back from the job.
        var title = Blank(album)
            ?? Blank(_dvd?.VolumeName)
            ?? Loc.Get("Rip_ChipKicker");
        var drive = _disc?.Drive ?? _dvd?.Drive;
        bool isDvd = _kind == DiscKind.Dvd;

        _started = _rips.Start(title, isDvd ? RipSource.Dvd : RipSource.Cd, drive, (hooks, ct) => isDvd
            ? RipDvdAsync(trackPlans, format, hooks, ct)
            : RipCdAsync(trackPlans, format, hooks, ct));

        OnRipChanged();
    }

    /// <summary>Repaints the rip face from the job. Every change arrives here: a progress tick, a track
    /// landing, or the whole rip finishing or failing. It arrives whether this window started the rip or
    /// opened onto one already running.</summary>
    private void OnRipChanged()
    {
        if (_face != Face.Rip)
            return;

        RipStatusText.Text = _rips.Status;
        RipProgressBar.Value = _rips.Fraction;
        RipProgressBar.Visibility = _rips.State == RipJobState.Failed
            ? Visibility.Collapsed
            : Visibility.Visible;
        RipDetailText.Text = _rips.Detail;
        RippedList.Visibility = _rips.Tracks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Skip and stop only mean something while the drive is turning.
        RipActions.Visibility = _rips.CanSteer ? Visibility.Visible : Visibility.Collapsed;
        RenderFace();

        // Idle with nothing to import means the rip was cancelled. Its staging folder, and every track in
        // the list, is gone. The rip face has nothing left to show, so the disc gets its form back.
        if (_started && _rips.State == RipJobState.Idle && !_rips.HasCompleted)
        {
            _started = false;
            // A window opened onto someone else's rip has no disc of its own to go back to. So a cancel
            // leaves nothing to show behind it.
            if (_disc is null && _dvd is null)
                Hide();
            else
                GoTo(Face.Disc);
        }
    }

    /// <summary>A finished rip hands its folder to the import queue by itself. So there is nothing left
    /// for this window to report, and it closes.</summary>
    private void OnRipFinished() => Hide();

    private Task<RipResult> RipCdAsync(IReadOnlyList<RipTrackPlan> tracks, RipFormat format,
        RipHooks hooks, CancellationToken ct)
    {
        if (_disc is null)
            throw new InvalidOperationException("no CD to rip");

        var plan = new RipPlan(_disc, tracks, format, _sleeve, _discArt);
        return _ripper.RipAsync(plan, _stagingRoot, VerifyCheck.IsChecked == true, _stagingLayout,
            hooks.Progress, ct, hooks);
    }

    private Task<RipResult> RipDvdAsync(IReadOnlyList<RipTrackPlan> tracks, RipFormat format,
        RipHooks hooks, CancellationToken ct)
    {
        if (_dvd is null || _dvdTitle is null)
            throw new InvalidOperationException("no DVD title to rip");

        var audioIndex = Math.Max(0, AudioCombo.SelectedIndex);
        if (audioIndex >= _dvdTitle.AudioStreams.Count)
            throw new InvalidOperationException("this DVD title has no audio to rip");

        var plan = new DvdRipPlan(_dvd, _dvdTitle, _dvdTitle.AudioStreams[audioIndex], tracks, format,
            _sleeve, _discArt);
        return _ripper.RipDvdAsync(plan, _stagingRoot, _stagingLayout, hooks.Progress, ct, hooks);
    }

    // ── Previewing a ripped track ──
    //
    // The ripped file is still in staging, waiting for the import queue. Playing it is the job's
    // business, not this popup's. The deck's chip offers the same button on the same rows, so the click
    // is passed on at once.

    private void RippedPlay_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RippedTrackRow row)
            _rips.TogglePreview(row);
    }

    // ── Steering a rip in flight ──
    //
    // Three endings, three buttons, because each one leaves the disc in a different place. Skip loses one
    // track. Stop keeps the tracks already ripped. Cancel keeps nothing.

    private void SkipTrack_Click(object sender, RoutedEventArgs e) => _rips.SkipTrack();

    private void StopRip_Click(object sender, RoutedEventArgs e) => _rips.Stop();
}

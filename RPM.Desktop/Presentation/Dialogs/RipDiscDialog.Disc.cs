using RPM.Core.Diagnostics;
using RPM.Core.Net;
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
/// Reads what is in the drive: the CD's table of contents, or a DVD's titles.
/// </summary>
public sealed partial class RipDiscDialog
{
    // ── The disc ─────────────────────────────────────────────────────────────────────────────

    private async Task ScanDrivesAsync()
    {
        SetDiscBusy(true, Loc.Get("AddMusic_LookingForDrive"));
        try
        {
            _drives.Clear();
            _drives.AddRange(await _ripper.ListDrivesAsync());

            DriveCombo.Items.Clear();
            foreach (var drive in _drives)
                DriveCombo.Items.Add(drive.DisplayName);

            if (_drives.Count == 0)
            {
                SetDiscBusy(false, Loc.Get("AddMusic_NoDrive"));
                return;
            }

            DriveCombo.SelectedIndex = 0; // fires the selection handler, which reads the disc
        }
        catch (Exception ex)
        {
            SetDiscBusy(false, Loc.Get(DiscTroubleKey(ex)));
        }
    }

    private async void Drive_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DriveCombo.SelectedIndex < 0 || DriveCombo.SelectedIndex >= _drives.Count)
            return;

        await ReadDiscAsync(_drives[DriveCombo.SelectedIndex]);
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e) => await ScanDrivesAsync();

    private async Task ReadDiscAsync(DiscDrive drive)
    {
        _disc = null;
        _dvd = null;
        _dvdTitle = null;
        _tracks.Clear();
        RebuildRows();
        TracksPane.Visibility = Visibility.Collapsed;
        FormatGroup.Visibility = Visibility.Collapsed;
        DvdTitleGroup.Visibility = Visibility.Collapsed;
        MetaGroup.Visibility = Visibility.Collapsed;
        ResetMeta();
        UpdateRipEnabled();

        if (_kind == DiscKind.Dvd)
        {
            await ReadDvdAsync(drive);
            return;
        }

        SetDiscBusy(true, Loc.Get("AddMusic_ReadingDisc"));
        try
        {
            var disc = await _ripper.ReadDiscAsync(drive);
            if (disc is null)
            {
                SetDiscBusy(false, Loc.Get("AddMusic_NoDisc"));
                return;
            }

            _disc = disc;

            // Fill the list from the disc's own table of contents first. An unknown disc can still be
            // ripped. The lookup only improves these rows. It is never needed before a rip.
            foreach (var track in disc.Toc.AudioTracks)
            {
                var length = disc.Toc.DurationOf(track);
                AddTrackRow(new RipTrackRow(track.Number, Format(length))
                {
                    Seconds = length.TotalSeconds,
                    Title = Loc.Format("AddMusic_TrackN", track.Number),
                });
            }

            RebuildRows();
            TracksPane.Visibility = Visibility.Visible;
            FormatGroup.Visibility = Visibility.Visible;
            // A CD names itself, but the metadata can be edited and looked up like any other disc. So the
            // album card shows as soon as the tracks do, even before the lookup answers.
            MetaGroup.Visibility = Visibility.Visible;
            UpdateRipEnabled();

            SetDiscBusy(true, Loc.Get("AddMusic_LookingUp"));
            await LookupAsync(disc);
        }
        catch (Exception ex)
        {
            SetDiscBusy(false, Loc.Get(DiscTroubleKey(ex)));
        }
    }

    /// <summary>Names the disc from MusicBrainz and fills the editable album fields. A miss is not a
    /// failure. The disc still rips, the tracks keep their "Track 3" placeholders, and the user can look
    /// up a pressing by hand, as they would for a DVD.</summary>
    private async Task LookupAsync(DiscInfo disc)
    {
        // Nobody is signed in, so this disc is not looked up. Otherwise "could not identify the disc"
        // would report a miss that never happened. Only the fields are affected. The rip runs either way,
        // so this is a status line, not a dialog in the way.
        if (!App.Services.GetRequiredService<Services.Account.AccountGate>().IsOpen)
        {
            SetDiscBusy(false, Loc.Get("AddMusic_LookupSignIn"));
            return;
        }

        try
        {
            // "Could not identify the disc" is the catalogue's answer. Not getting an answer is
            // different: the disc may be well known.
            if (!(await _ripper.LookupDiscAsync(disc)).Answers(out var release, out _))
            {
                SetDiscBusy(false, Loc.Get("AddMusic_LookupFailed"));
                return;
            }

            if (release is null)
            {
                SetDiscBusy(false, Loc.Get("AddMusic_UnknownDisc"));
                return;
            }

            AlbumBox.Text = release.Title ?? "";
            ArtistBox.Text = release.Artist ?? "";
            YearBox.Text = release.Year > 0 ? release.Year.ToString() : "";

            // The whole listing, not only its titles. A disc that names itself carries its composers, its
            // ISRCs and its advisory flags, and on a classical CD the works its tracks are movements of.
            // A rip is the one moment any of this can reach the files.
            ApplyListing(
                [.. release.Tracks.Select((t, i) =>
                    RpmMetadataProvider.Adopt(t, release.Artist, (uint)i + 1))],
                [.. _tracks]);

            SetDiscBusy(false, "");

            // The disc named itself, so its art also comes over. The sleeve provider keys on the rpm
            // release id that the lookup returned. Both pictures fall back to a lookup by name when the
            // archive has no scan under that id. This is best effort. Missing art is normal, and it must
            // never hold up the rip.
            var found = new ReleaseSearchResult(
                new ReleaseRef(RpmMetadataProvider.ProviderId, release.ReleaseId),
                release.Title ?? "", release.Artist, release.Year,
                Date: null, Country: null, Format: null, TrackCount: release.Tracks.Count);
            await FetchArtAsync(found, settled: "");
        }
        catch (Exception)
        {
            // Offline, rate-limited, or failed for another reason: the disc is still rippable.
            SetDiscBusy(false, Loc.Get("AddMusic_LookupFailed"));
        }
    }

    // ── The DVD ──────────────────────────────────────────────────────────────────────────────

    private async Task ReadDvdAsync(DiscDrive drive)
    {
        SetDiscBusy(true, Loc.Get("AddMusic_ReadingDvd"));
        try
        {
            var dvd = await _ripper.ReadDvdAsync(drive);
            if (dvd is null)
            {
                SetDiscBusy(false, Loc.Get("AddMusic_NoDvd"));
                return;
            }

            // A commercial disc is scrambled with CSS. rpm can descramble it, but only with a libdvdcss
            // that the user has installed. rpm does not ship it, because it is GPL. Without it there is
            // nothing to rip. So say so, with the one-line fix, instead of failing in the middle of a
            // rip.
            if (dvd.Titles.All(DvdReader.IsEncrypted) && !DvdReader.LibDvdCssAvailable())
            {
                // The fix differs by OS. macOS has a package manager and one command. Windows has
                // neither, so the app offers to take the downloaded file and install it itself.
                SetDiscBusy(false, Loc.Get(OperatingSystem.IsWindows()
                    ? "AddMusic_DvdNeedsLibDvdCssWindows"
                    : "AddMusic_DvdNeedsLibDvdCss"));
                if (OperatingSystem.IsWindows())
                {
                    _pendingCssDrive = drive;
                    ChooseLibDvdCssButton.Visibility = Visibility.Visible;
                }
                return;
            }

            _dvd = dvd;

            TitleCombo.Items.Clear();
            foreach (var title in dvd.Titles)
            {
                TitleCombo.Items.Add(Loc.Format("AddMusic_TitleN",
                    title.Number, Format(title.Duration), title.Chapters.Count));
            }

            DvdTitleGroup.Visibility = Visibility.Visible;

            // A DVD has no disc ID and no track titles, so nothing fills itself in. The volume label is
            // the only name it carries, for example "DAVIDGRAY" or "THE_WALL_LIVE". It seeds the album
            // box for the lookup. It is not trusted as an answer. The user finishes with the Look up
            // button, the same one a CD offers.
            MetaGroup.Visibility = Visibility.Visible;
            AlbumBox.Text = ReadableVolumeName(dvd.VolumeName);

            SetDiscBusy(false, "");

            // Titles arrive longest first, so the main feature is already the right default.
            TitleCombo.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            SetDiscBusy(false, Loc.Get(DiscTroubleKey(ex)));
        }
    }
}

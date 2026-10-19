using RPM.Core.Net;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using RPM.Core.Analysis;
using RPM.Core.Art;
using RPM.Core.Library;
using RPM.Core.Metadata.Providers;
using RPM.Core.Models;
using RPM.Core.Ripping;
using RPM.Core.Splitting;
using RPM.Desktop.Services;
using RPM.Desktop.Strings;
using RPM.Fingerprint.Catalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>
/// Names the disc from the catalogues, so the tracks are filed under what they really are, not Track 01.
/// </summary>
public sealed partial class RipDiscDialog
{
    // ── Naming the disc from the catalogues ──
    //
    // A CD names itself from its disc ID. That answer is not always the right pressing. A DVD has no disc
    // ID and names nothing. Both offer the same Look up button, which opens the release picker used by
    // the split dialog. Whatever the user settles on fills the album fields, renames the tracks, and
    // brings the sleeve over. A disc cannot supply the sleeve from itself.

    /// <summary>
    /// A DVD's volume label is written for a filesystem, not for a person. It is upper case, has no
    /// spaces, and uses underscores for characters it could not keep ("LIVE_IN_SLOW_MOTION"). This gets
    /// it close enough to seed the lookup. A label run together, like "DAVIDGRAY", cannot be fully
    /// recovered. The box is editable for exactly that reason.
    /// </summary>
    private static string ReadableVolumeName(string? volume)
    {
        if (string.IsNullOrWhiteSpace(volume))
            return "";

        var words = volume.Replace('_', ' ').Replace('.', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // ALL CAPS reads as shouting and matches worse. Leave a label that is already mixed case alone.
        return string.Join(' ', words.Select(w =>
            w.All(char.IsUpper) && w.Length > 1
                ? char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()
                : w));
    }

    /// <summary>The lengths of the tracks, in order. A candidate release's listing is scored against them
    /// in the lookup. A CD's lengths come from its table of contents. A DVD's come from its chapter
    /// marks.</summary>
    private IReadOnlyList<double> DetectedLengths() => _tracks.Select(t => t.Seconds).ToList();

    /// <summary>Opens the shared release picker, seeded with what the album fields already hold, and
    /// takes over whatever the user settles on. The medium starts on the disc in the drive, because that
    /// is the pressing being named. The user can widen it from there.</summary>
    private async void Lookup_Click(object sender, RoutedEventArgs e)
    {
        // The rip itself needs no account. Naming it from the catalogues does. So the sign-in check is on
        // this button, not on the window. A signed-out user can still rip the disc and type the titles
        // in.
        if (!await SignInGate.AllowAsync(XamlRoot, Loc.Get("Login_ForLookup")))
            return;

        uint.TryParse(YearBox.Text, out var year);
        // There is no track step, because there are no files yet. The listing names the rip as it is
        // written.
        var picked = await ReleaseLookupDialog.ShowAsync(
            XamlRoot!, _catalog, Models.TrackMetadataStep.None, DetectedLengths(),
            Blank(AlbumBox.Text), Blank(ArtistBox.Text),
            _kind == DiscKind.Dvd ? ReleaseMedia.Dvd : ReleaseMedia.Cd,
            Blank(CountryBox.Text), year);
        if (picked is not null)
            await AdoptReleaseAsync(picked.Release, picked.Detail);
    }

    /// <summary>
    /// Takes the release the user picked from the lookup. Its name, artist, year and country fill the
    /// album fields. Its listing renames the tracks, and its sleeve is fetched to embed. On a DVD the
    /// titles go onto the TICKED chapters in order. A concert disc has an intro and a credit roll that
    /// are not on the track listing, so unticking those two puts the setlist in the right place. On a CD
    /// every row is named in order. Every title stays editable afterwards.
    /// </summary>
    private async Task AdoptReleaseAsync(ReleaseSearchResult chosen, ReleaseDetail? prefetched)
    {
        SetMetaStatus(Loc.Format("AddMusic_LookupFetching", chosen.Title));

        // The user has just said which pressing this is. That is the answer the automatic lookup could
        // not find, so "could not identify the disc" no longer applies.
        SetDiscBusy(false, "");

        // A release with no listing still names the record. The header fields fill from the search result
        // either way. The status line says different things for the two cases, because a release with no
        // listing and a listing that did not arrive need different sentences.
        ReleaseDetail? detail = prefetched;
        var unanswered = false;
        if (detail is null)
        {
            try
            {
                var provider = _catalog.Search.Available.FirstOrDefault(p => p.Id == chosen.Ref.Provider);
                if (provider is not null)
                {
                    if ((await provider.GetReleaseAsync(chosen.Ref.Id, CancellationToken.None)).Answers(out var fetched, out _))
                        detail = fetched;
                    else
                        unanswered = true;
                }
            }
            catch
            {
                unanswered = true;
            }
        }

        AlbumBox.Text = detail?.Title ?? chosen.Title ?? "";
        var artist = detail?.Artist ?? chosen.Artist;
        if (!string.IsNullOrWhiteSpace(artist)) ArtistBox.Text = artist;
        var year = detail is { Year: > 0 } ? detail.Year : chosen.Year;
        if (year > 0) YearBox.Text = year.ToString();
        var country = detail?.Country ?? chosen.Country;
        if (!string.IsNullOrWhiteSpace(country)) CountryBox.Text = country;

        // A multi-disc set fills the disc picker, which is matched to the CD in the drive. Only the
        // chosen disc's tracks name this CD. Mapping the whole continuous listing would put disc 1's
        // titles on disc 2. A single-disc release names the rows straight through.
        SetupDiscSelection(detail);

        await FetchArtAsync(chosen, settled:
            unanswered ? Loc.Get("Search_ListingUnanswered")
            : detail is not { Tracks.Count: > 0 } ? Loc.Get("Search_NoListing")
            : "");
    }

    /// <summary>Fills the disc picker from the picked release, and names the rows from the right disc. On
    /// a multi-disc set the disc is matched to the drive's track count and lengths, and it stays
    /// editable. On a single-disc release the picker is hidden and no disc tags are written.</summary>
    private void SetupDiscSelection(ReleaseDetail? detail)
    {
        _release = detail;
        _discArt = null;   // the user picked a release; its own art is used, not the auto-fetched disc
        _discNumber = 0;
        _discCount = 0;
        _discSubtitle = null;

        if (detail is { DiscCount: > 1 })
        {
            // The catalogue says the release is a set, and which discs it has. Its answer wins. The disc
            // is matched to the CD in the drive by its track count and lengths.
            uint best = Math.Clamp(DiscMatcher.BestDisc(detail, DetectedLengths(), _tracks.Count), 1, detail.DiscCount);
            FillDiscBoxes(best, detail.DiscCount);
            SetDiscHint(Loc.Format("AddMusic_DiscFromRelease", detail.DiscCount));
        }
        else
        {
            // There is no release, or the release says nothing about discs, or it lists a reissue as
            // separate single-disc releases. In those cases the library is asked instead. The library
            // knows what is already on the shelf.
            ProposeDiscFromLibrary();
        }

        ApplyReleaseTitles();
    }

    /// <summary>
    /// Fills the disc boxes from what the library already holds for this album.
    ///
    /// <para>A rip whose album name matches an album in the library is another disc of that album. The
    /// naming template files it into that folder in any case, so the only question is which disc number
    /// it gets. <see cref="DiscProposals"/> answers it. That includes the case that would otherwise
    /// corrupt the library quietly: a disc the album already has, ripped again because the first attempt
    /// was bad.</para>
    ///
    /// <para>This is a proposal, never a decision. Nothing here can know that a disc ripped second is
    /// disc two, because CD2 may have gone in first. So the number is shown with its reason, and both
    /// boxes stay editable.</para>
    /// </summary>
    private void ProposeDiscFromLibrary()
    {
        var album = Blank(AlbumBox.Text);
        var artist = Blank(ArtistBox.Text);

        // Separate-album mode means the user wants each disc as its own record. There is nothing to join,
        // so proposing a disc number would go against their own setting.
        var settings = App.Services.GetRequiredService<SettingsService>();
        if (album is null || settings.MultiDiscMode != MultiDiscMode.MergedAlbum)
        {
            FillDiscBoxes(1, 1);
            SetDiscHint(null);
            return;
        }

        var library = App.Services.GetRequiredService<LibraryService>();
        List<(uint Disc, double Seconds)> existing = [];
        string? folder = null;
        // If the library did not answer, the proposal uses the disc alone. An unreadable library is not a
        // reason to block a rip. A refusal has already put the problem screen up behind this dialog (see
        // LibraryService.Read).
        if (library.IsOpen
            && library.Read(library.Db, "the discs already filed for a rip", db =>
                   db.FindAlbumFolder(album, artist) is { } found
                       ? (Folder: (string?)found, Tracks: db.QueryUnderFolder(found))
                       : (Folder: null, Tracks: Array.Empty<LibraryTrack>()))
               .Answers(out var filed, out _))
        {
            folder = filed.Folder;
            existing = [.. filed.Tracks.Select(t => (t.DiscNumber, t.DurationSeconds))];
        }

        var proposal = DiscProposals.For(existing, DetectedLengths());
        FillDiscBoxes(proposal.Disc, proposal.DiscCount);

        SetDiscHint(proposal.Kind switch
        {
            DiscProposalKind.NextDisc =>
                Loc.Format("AddMusic_DiscFromLibrary", album, existing.Count, proposal.Disc),
            DiscProposalKind.ReRip =>
                Loc.Format("AddMusic_DiscReRip", proposal.Disc),
            _ => null,
        });
    }

    /// <summary>Fills both boxes with a chosen disc and a chosen count. The count can still be raised
    /// above either value. The proposal is a starting point, and the user may know better.</summary>
    private void FillDiscBoxes(uint disc, uint count)
    {
        var top = Math.Max(Math.Max(disc, count) + 2, 6);

        _settingDisc = true;
        DiscCombo.Items.Clear();
        DiscCountCombo.Items.Clear();
        for (uint d = 1; d <= top; d++)
        {
            DiscCombo.Items.Add(d.ToString());
            DiscCountCombo.Items.Add(d.ToString());
        }
        DiscCombo.SelectedIndex = (int)disc - 1;
        DiscCountCombo.SelectedIndex = (int)Math.Max(count, disc) - 1;
        _settingDisc = false;
    }

    private void SetDiscHint(string? text)
    {
        DiscHintText.Text = text ?? "";
        DiscHintText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The album name is the key the library is asked by, so a correction to it asks again. This
    /// happens only when no release is driving the disc. An adopted set already knows its own
    /// shape.</summary>
    private void OnAlbumIdentityChanged(object sender, TextChangedEventArgs e)
    {
        if (_settingDisc || _release is { DiscCount: > 1 })
            return;
        ProposeDiscFromLibrary();
        ReadDiscBoxes();
    }

    private void OnDiscSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_settingDisc)
            return;

        // A number set by hand is the user's own answer. The reason for the app's guess no longer
        // applies.
        SetDiscHint(null);
        ApplyReleaseTitles();
    }

    /// <summary>
    /// Reads the disc and the count from the boxes. "Disc 1 of 1" is a single CD and writes nothing. A
    /// lone album that carries DISCNUMBER 1 and DISCTOTAL 1 is noise in every tag editor, and the
    /// tracklist treats an untagged disc as disc one anyway.
    /// </summary>
    private void ReadDiscBoxes()
    {
        var disc = (uint)Math.Max(DiscCombo.SelectedIndex + 1, 1);
        var count = (uint)Math.Max(DiscCountCombo.SelectedIndex + 1, 1);

        // "Disc 2 of 1" is a typing slip, not an instruction.
        count = Math.Max(count, disc);

        if (count <= 1)
        {
            _discNumber = 0;
            _discCount = 0;
            _discSubtitle = null;
            return;
        }

        _discNumber = disc;
        _discCount = count;
    }

    /// <summary>Names the rip rows from the chosen disc's listing, or from the whole listing when the
    /// release is a single disc. It also records which disc this CD is, so the rip writes "disc N of M"
    /// and the subtitle.</summary>
    private void ApplyReleaseTitles()
    {
        // The boxes hold the answer now, whether a release or the library set them. Read them first, so a
        // rip with no catalogue match still writes a disc number. That is the whole point.
        ReadDiscBoxes();

        if (_release is null)
            return;

        IReadOnlyList<ReleaseTrackInfo> titles;
        if (_release.DiscCount > 1 && DiscCombo.SelectedIndex >= 0)
        {
            var discTracks = DiscMatcher.TracksOf(_release, _discNumber);
            // A release that gives a disc count but no listing for each disc still sets the disc number.
            // The rows keep whatever titles they had, and are not cleared.
            _discSubtitle = discTracks.FirstOrDefault()?.DiscSubtitle;
            titles = discTracks.Count > 0 ? discTracks : System.Array.Empty<ReleaseTrackInfo>();
        }
        else
        {
            titles = _release.Tracks;
        }

        ApplyListing(titles, _kind == DiscKind.Dvd
            ? _tracks.Where(t => t.IsChecked).ToList()
            : _tracks.ToList());
    }

    /// <summary>
    /// Names rows from a listing, in order: the title, the work the track is a movement of, and the
    /// catalogue's full answer behind both.
    ///
    /// <para>Two things name a disc: the CD's own disc-ID lookup, and a pressing the user picked. Both
    /// use this method, so both carry the same details past the title: composer, ISRCs and works.</para>
    /// </summary>
    private void ApplyListing(IReadOnlyList<ReleaseTrackInfo> titles, IReadOnlyList<RipTrackRow> targets)
    {
        for (var i = 0; i < targets.Count && i < titles.Count; i++)
        {
            var listing = titles[i];
            var row = targets[i];

            // The rest of the listing's answer for this row: its own artist, its work and movement, its
            // ISRC, and whether it is explicit. The rip reads it from the row when it builds the plan.
            row.Catalogue = listing;

            // A classical listing is a list of works, each with its movements under it, and it is drawn
            // that way. The row holds the movement alone. The work is one line above, over the whole run
            // of them.
            row.Work = Blank(listing.Work);

            var title = row.Work is null ? listing.Title : Blank(listing.MovementName) ?? listing.Title;
            if (!string.IsNullOrWhiteSpace(title))
                row.Title = title;
        }

        // Rows the listing does not reach keep the title they had, because the user may have typed it.
        // They never keep a work from the pressing before this one. That would file a track under a piece
        // it is not part of.
        for (var i = titles.Count; i < targets.Count; i++)
            targets[i].Work = null;

        // A pressing that arrives with works has answered the question the switch asks, so it sets the
        // switch. It never clears the switch. A user who said this disc is classical and typed part of
        // its structure in is not overruled by a lookup that returned none.
        if (_tracks.Any(t => t.Work is not null))
            SetClassical(true);

        RebuildRows();
    }

    /// <summary>Forgets any disc structure taken from a picked release. This happens when the disc in the
    /// drive changes, or when an automatic disc-ID lookup names the disc directly.</summary>
    private void ClearDiscSelection()
    {
        _release = null;
        _discSubtitle = null;
        // The listing goes with it. Keeping the old pressing's credits on the rows would stamp one
        // record's composer and ISRCs onto another record's rip. Its works would also still be printed
        // over tracks that no longer belong to them.
        foreach (var track in _tracks)
        {
            track.Catalogue = null;
            track.Work = null;
        }
        // The works went with the listing, so the tree is one level again.
        RebuildRows();
        // Go back to what the library says. The panel stays up. The disc in the drive is still a disc,
        // and losing the release is exactly when the user most needs to say which one it is.
        ProposeDiscFromLibrary();
    }

    /// <summary>
    /// Fetches both pictures a rip can carry, the sleeve and the label on the disc, and holds them for
    /// the rip. This is best effort throughout. Missing art is normal, and it must never hold up a rip.
    ///
    /// <para>Both are fetched here, however the disc was named: by its own disc ID or by a pressing the
    /// user picked. A rip is the one moment they can be had. The tracks come off the disc with no art of
    /// their own, and nothing looks a staged rip up again later.</para>
    /// </summary>
    /// <param name="release">The pressing to ask the catalogue's art archive for, when there is one. Null
    /// goes straight to the lookup by name.</param>
    /// <param name="settled">What the status line says once the art is in. Each caller states it, because
    /// this is the last write to that line. A caller with something to report, such as a listing that did
    /// not arrive, passes it here. Writing it earlier would clear it.</param>
    private async Task FetchArtAsync(ReleaseSearchResult? release, string settled)
    {
        _lookupCts?.Cancel();
        var cts = new CancellationTokenSource();
        _lookupCts = cts;

        var artist = Blank(ArtistBox.Text);
        var album = Blank(AlbumBox.Text);

        try
        {
            // The art goes on the rip without an announcement. Clear the lookup status, so the "Looking
            // up" line does not stay once the sleeve has arrived.
            if (release is not null)
                _sleeve = await _catalog.SleeveAsync(release, cts.Token);

            // The archive that answers for a release id has no scan for many pressings. The artwork
            // service answers on artist and album instead. So a disc the archive has never heard of still
            // gets its cover.
            if (_sleeve is null && await _artwork.CoverBytesAsync(artist, album, cts.Token) is { } scan)
                _sleeve = SleeveScaler.ToSleeve(scan);

            // The disc art is also looked up by name. That is why it is fetched here and not only on the
            // disc-ID path. Picking the pressing by hand must still bring the label picture over.
            if (_discArt is not { Length: > 0 })
                _discArt = await _artwork.DiscArtBytesAsync(artist, album, cts.Token);

            if (!cts.IsCancellationRequested)
                SetMetaStatus(settled);
        }
        catch (OperationCanceledException) { }
        catch { SetMetaStatus(settled); }
    }

    private void SetMetaStatus(string? text)
    {
        MetaStatusText.Text = text ?? "";
        MetaStatusText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Clears the editable album fields and the held sleeve. The disc in the drive changed, so
    /// nothing carried over from the last one is still true.</summary>
    private void ResetMeta()
    {
        ClearDiscSelection();
        AlbumBox.Text = "";
        ArtistBox.Text = "";
        YearBox.Text = "";
        CountryBox.Text = "";
        _sleeve = null;
        _discArt = null;
        _lookupCts?.Cancel();
        SetMetaStatus(null);
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private void Title_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_dvd is null || TitleCombo.SelectedIndex < 0 || TitleCombo.SelectedIndex >= _dvd.Titles.Count)
            return;

        var title = _dvd.Titles[TitleCombo.SelectedIndex];
        _dvdTitle = title;

        AudioCombo.Items.Clear();
        foreach (var audio in title.AudioStreams)
            AudioCombo.Items.Add(audio.DisplayName);
        AudioCombo.SelectedIndex = title.AudioStreams.Count > 0 ? 0 : -1;

        // A chapter becomes a track. The names are placeholders, because a DVD carries no track titles.
        // The user types the setlist in here.
        _tracks.Clear();
        foreach (var chapter in title.Chapters)
        {
            AddTrackRow(new RipTrackRow(chapter.Number, Format(chapter.Duration))
            {
                Seconds = chapter.Duration.TotalSeconds,
                Title = Loc.Format("AddMusic_TrackN", chapter.Number),
            });
        }

        RebuildRows();
        TracksPane.Visibility = Visibility.Visible;
        FormatGroup.Visibility = Visibility.Visible;
        UpdateRipEnabled();
    }

    /// <summary>Adds a track row and keeps the Rip button in step with its checkbox. Ticking a row by
    /// hand changes the row, not the dialog, so nothing else would re-check whether a rip is now
    /// possible.</summary>
    private void AddTrackRow(RipTrackRow row)
    {
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RipTrackRow.IsChecked))
                UpdateRipEnabled();
        };
        _tracks.Add(row);
    }

    /// <summary>
    /// Redraws the list as the two-level tree that a classical listing is: a work, with its movements
    /// indented under it.
    ///
    /// <para>The tree is built from the tracks, not kept beside them. The tracks are the disc, and the
    /// tree is only a reading of them. A rip takes <see cref="_tracks"/> whatever the list looks like.
    /// Runs come from the library's own grouping rule. So a work played twice on one disc has two
    /// headings, and a piece that belongs to no work keeps its place in the order.</para>
    /// </summary>
    private void RebuildRows()
    {
        _rows.Clear();
        foreach (var track in _tracks)
            track.Classical = _classical;

        foreach (var (work, tracks) in AlbumTracklist.ByWork(_tracks, t => t.Work))
        {
            if (work is not null)
            {
                var row = new RipWorkRow(work, tracks);
                // Emptying a work's box removes the work from its movements. They become ordinary tracks
                // again, and the line that headed them has nothing left to say. Only an empty box does
                // this. A rename keeps the run as it is. Redrawing the list under a user who is typing
                // would take the box away from the caret.
                row.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(RipWorkRow.Title) && string.IsNullOrWhiteSpace(row.Title))
                        RebuildRows();
                };
                _rows.Add(row);
            }

            foreach (var track in tracks)
                _rows.Add(track);
        }
    }

    /// <summary>
    /// Whether this disc is a classical record. Here that means "let me say what the works are".
    ///
    /// <para>A pressing the catalogues know arrives grouped, and that switches this on by itself. The
    /// case this is for is the other one: a disc nothing has identified, whose structure is printed in
    /// the booklet and nowhere else. When it is off, the list is the flat list it has always been. When
    /// it is on, every line gets a control for putting it under a work or taking it back out.</para>
    /// </summary>
    private bool _classical;

    private void ClassicalCheck_Click(object sender, RoutedEventArgs e)
    {
        SetClassical(ClassicalCheck.IsChecked == true);

        // Turning it off means the user is saying this is not that kind of record. So the works go with
        // it. Otherwise they would stay as a grouping with no way left to see or edit it.
        if (!_classical)
            foreach (var track in _tracks)
                track.Work = null;

        RebuildRows();
    }

    /// <summary>Sets the switch and the box together. A pressing that arrives with works turns it on, and
    /// so does the user. Both are the same act.</summary>
    private void SetClassical(bool on)
    {
        _classical = on;
        ClassicalCheck.IsChecked = on;
    }

    /// <summary>
    /// Puts a track under a work: the work above it, if there is one, or a new work named after the track
    /// itself if there is not.
    ///
    /// <para>It is named after the track because that is where the name usually is on a disc nobody has
    /// catalogued. The booklet's "Sonata in C, K330: I. Allegro moderato" gets typed into the title box.
    /// The first movement of a piece carries the piece's name into the heading, and the user trims it to
    /// the work and leaves the movement behind. A guess to correct is better than an empty box to
    /// fill.</para>
    /// </summary>
    private void JoinWork_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RipTrackRow row)
            return;

        var index = _tracks.IndexOf(row);
        if (index < 0)
            return;

        row.Work = index > 0 && _tracks[index - 1].Work is { } above ? above : Blank(row.Title) ?? " ";
        RebuildRows();
    }

    /// <summary>Takes a track back out of its work. It is a piece in its own right again.</summary>
    private void LeaveWork_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RipTrackRow row)
            row.Work = null;
        RebuildRows();
    }

    /// <summary>The parent tick: takes the whole work, or leaves it. It is handled here and not with a
    /// two-way binding, because a three-state box cycles through its third state when clicked. "Half of
    /// this work" is something the list reports. The user never asks for it.</summary>
    private void WorkCheck_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RipWorkRow work)
            work.Toggle();
        UpdateRipEnabled();
    }

    private void AllTracks_Click(object sender, RoutedEventArgs e)
    {
        var check = AllTracksCheck.IsChecked == true;
        foreach (var track in _tracks)
            track.IsChecked = check;
        UpdateRipEnabled();
    }
}

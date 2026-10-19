using RPM.Core.Diagnostics;
using RPM.Core.Net;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using RPM.Core.Metadata.Providers;
using RPM.Core.Models;
using RPM.Core.Recording;
using RPM.Core.Ripping;
using RPM.Core.Splitting;
using RPM.Desktop.Services;
using RPM.Desktop.Strings;
using RPM.Fingerprint.Catalog;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>
/// Gives the cut tracks names. This is done in two ways: by recognising them from the audio, and by
/// naming the record itself from the catalogues.
/// </summary>
public sealed partial class SplitAlbumDialog
{
    // ── Recognising tracks from the audio ──
    //
    // Recognition is per track, not for the whole album. The user ticks the tracks to name, and each one
    // is looked up on its own. The basic layers run first. Then the advanced layer (AudD) runs for the
    // tracks the basic layers could not name. This is the same order as in the Recognize window. Only
    // supporters get the advanced layer. MainModel decides this when the window opens (see
    // RecognitionReach.ForSplit), and the button is hidden otherwise.

    private async void RecognizeSelected_Click(object sender, RoutedEventArgs e) => await RecognizeSelectedAsync();

    private async Task RecognizeSelectedAsync()
    {
        if (_busy || _analysis is null || !_analyzer.CanIdentify)
            return;
        // Tracks are matched by identity. An edit made while this runs must not send a result to
        // whichever track has moved into that position.
        var ticked = _state.TickedTracks.Select(t => t.Id).ToList();
        if (ticked.Count == 0)
            return;

        SetBusy(true);
        _cts = new CancellationTokenSource();

        // The advanced layer's reach, as this run holds it. The server can take the advanced layer away
        // part-way through, if support ends while the window is open. The rest of the run then carries on
        // without it.
        bool advanced = _analyzer.CanReachAdvanced;
        string? refusedNote = null;
        int named = 0, failed = 0;
        string? lastError = null;
        try
        {
            int done = 0;
            foreach (var id in ticked)
            {
                _cts.Token.ThrowIfCancellationRequested();
                if (_state.Track(id) is not { } track)
                    continue;   // dropped while we were working
                SetFinalStatus(Loc.Format("Split_RecognizingN", ++done, ticked.Count));
                try
                {
                    var res = await _analyzer.RecognizeTrackAsync(
                        _subject.FilePath, track.Start, track.Length, reachAdvanced: advanced, _cts.Token);
                    if (res is null)
                        continue;
                    Apply(_state.Rename(id, res.Title,
                        string.IsNullOrWhiteSpace(res.Artist) ? _state.Track(id)?.Artist : res.Artist));
                    named++;
                }
                catch (RPM.Core.Identify.LambdaIdentificationBackend.SupportersOnlyException)
                {
                    // This track keeps what it had. The basic layers already missed it.
                    advanced = false;
                    refusedNote = Loc.Get("Recognize_AdvancedRefused");
                }
                catch (Exception ex) when (ex is not OperationCanceledException
                                               and not RPM.Core.Identify.LambdaIdentificationBackend.IdentificationUnavailableException)
                {
                    // One track that cannot be read must not stop the others. It is counted separately,
                    // not folded into "not recognised", because the two mean different things to the
                    // user.
                    failed++;
                    lastError = Faults.Note("split", "recognizing a split track", ex).Say();
                }
            }

            string status = Loc.Format("Split_RecognizedN", named, ticked.Count);
            if (failed > 0)
                status += " " + Loc.Format("Split_RecognizeSomeFailed", failed, lastError ?? "");
            if (refusedNote is not null)
                status += " " + refusedNote;
            SetFinalStatus(status);
        }
        catch (OperationCanceledException) { }
        catch (RPM.Core.Identify.LambdaIdentificationBackend.IdentificationUnavailableException ex)
        {
            // The service cannot look anything up. Carrying on would give the same failure for every
            // track.
            Faults.Note("split", "recognition", ex);
            SetFinalStatus(Loc.Get("Split_RecognizeUnavailable"));
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ── Naming the record from the catalogues ──
    //
    // This is the other way to name a record. Recognition listens to the audio. This asks the user, who
    // has the sleeve and can read it. The lookup is a resizable dialog stacked over this one. Two
    // throttled catalogues are the wrong thing to attach to an autocomplete, so it has a search form of
    // its own. It returns the chosen pressing, which fills the header and aligns the boundaries.

    private async void Lookup_Click(object sender, RoutedEventArgs e)
    {
        // Cutting the record is this window's job, and it needs no account. Naming the pieces from a
        // catalogue does need one. So the sign-in check is on this button, not on the window that holds
        // it.
        if (!await SignInGate.AllowAsync(XamlRoot, Loc.Get("Login_ForLookup")))
            return;

        uint.TryParse(YearBox.Text, out var year);
        // A recorded record searches for records. An album file could have come from anywhere, so it
        // starts with no medium filter rather than a guessed one. There is no track step. This window has
        // its own grid of tracks, cut from the audio. The listing is aligned to those boundaries, not
        // written over files that do not exist yet.
        var picked = await ReleaseLookupDialog.ShowAsync(
            XamlRoot!, _catalog, Models.TrackMetadataStep.None, _state.Lengths,
            Blank(AlbumBox.Text), Blank(AlbumArtistBox.Text),
            _subject.Vinyl is not null ? ReleaseMedia.Vinyl : ReleaseMedia.Any,
            Blank(CountryBox.Text), year);
        if (picked is not null)
            await AdoptReleaseAsync(picked.Release, picked.Detail);
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>
    /// Takes the release the user picked in the lookup window. Its name and year fill the header. Its
    /// track listing is aligned to the boundaries already detected. This is the same alignment that
    /// identification uses, because it is the same job. The audio decides where the cuts are. The release
    /// only says what they are called.
    /// </summary>
    /// <param name="prefetched">The listing that the lookup window already fetched to score the match,
    /// when there was one. It is passed on, so the throttled lookup is not paid for twice.</param>
    private async Task AdoptReleaseAsync(ReleaseSearchResult chosen, ReleaseDetail? prefetched)
    {
        if (_busy || _analysis is null)
            return;

        SetBusy(true);
        var cts = new CancellationTokenSource();
        _adoptCts = cts;
        try
        {
            SetFinalStatus(Loc.Format("Search_Fetching", chosen.Title));
            var provider = _catalog.Search.Available.FirstOrDefault(p => p.Id == chosen.Ref.Provider);
            var detail = prefetched;
            var unanswered = false;
            if (detail is null && provider is not null)
            {
                if ((await provider.GetReleaseAsync(chosen.Ref.Id, cts.Token)).Answers(out var fetched, out _))
                    detail = fetched;
                else
                    unanswered = true;
            }

            // The pressing is kept whether or not its listing loads. A release with no tracklist still
            // has a sleeve, and the audio cannot supply that.
            _chosen = chosen;

            if (detail is null || detail.Tracks.Count == 0)
            {
                // The search result still counts. The user named the record, even if the listing does not
                // load. The status line says which case happened: the release has no listing, or the
                // listing did not arrive.
                ApplyReleaseHeader(chosen.Title, chosen.Artist, chosen.Year, chosen.Country);
                SetFinalStatus(Loc.Get(unanswered ? "Search_ListingUnanswered" : "Search_NoListing"));
                return;
            }

            ApplyReleaseHeader(detail.Title, detail.Artist ?? chosen.Artist,
                detail.Year != 0 ? detail.Year : chosen.Year, detail.Country ?? chosen.Country);

            var expected = detail.Tracks
                .Select(t => (t.Title, t.Artist, Length: t.LengthSeconds ?? 0))
                .ToList();

            // The album's lengths are used as per-row hints, and as the guide marks on the waveform.
            _albumLengths = expected.Select(t => t.Length).ToList();

            // These are names only. A release is a catalogue entry, not a measurement. Its times are
            // rounded to the second, usually come from another pressing, and know nothing about where
            // this transfer's needle went down. The cuts stay where they were found, or where they were
            // placed by hand. The release's times are offered, not imposed. They appear as the album
            // column beside each row, as the apply-timing button on the row, and as the gold guide marks
            // on the waveform. So a detection that has drifted from the sleeve can be seen and fixed by
            // hand.
            var plan = NameInOrder(detail.Tracks);

            // There is no second pass of the side trim here. This plan is the rows renamed. The rows were
            // already checked against the music when they were detected, and they have been edited since,
            // on purpose.
            _analysis = _analysis with { Plan = plan };
            LoadPlan(_analysis.Plan);
            RefreshAlbumHints();
            // There is no "album identified" line, because the user chose the release. Saying it back
            // would be noise.
            SetFinalStatus(Loc.Format("Split_TracksFound", _state.Count));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetFinalStatus(Loc.Format("Search_Failed", Faults.Note("split", "looking up the album", ex).Say()));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ApplyReleaseHeader(string album, string? artist, uint year, string? country)
    {
        AlbumBox.Text = album;
        if (!string.IsNullOrWhiteSpace(artist))
            AlbumArtistBox.Text = artist;
        if (year != 0)
            YearBox.Text = year.ToString();
        if (!string.IsNullOrWhiteSpace(country))
            CountryBox.Text = country;
    }

    /// <summary>Keeps every boundary exactly where it is, and takes the release's names in order, as far
    /// as they go.</summary>
    private SplitPlan NameInOrder(IReadOnlyList<ReleaseTrackInfo> tracks)
    {
        var named = BuildPlan().Tracks.Select((t, i) => i < tracks.Count
            ? t with
            {
                Title = tracks[i].Title,
                Artist = tracks[i].Artist ?? t.Artist,
                Catalogue = tracks[i],
            }
            : t).ToList();
        return _analysis!.Plan with { Tracks = named };
    }

    private async void Redetect_Click(object sender, RoutedEventArgs e) => await DetectAsync();

    private async void Split_Click(object sender, RoutedEventArgs e) => await SplitAsync();

    private async Task SplitAsync()
    {
        if (_busy || _analysis is null)
            return;

        // This is taken straight from the model. What is drawn on the waveform is what gets cut. The
        // needle-noise trim and the sliver merge belong to detection. Running them again here would
        // quietly move a cut placed by hand, and merge away a short interlude (20 s, for example) that the
        // user marked on purpose. The one-minute limit used there is right for a guess and wrong for a
        // decision.
        var plan = BuildPlan();
        if (plan.Tracks.Count < 2)
        {
            SetFinalStatus(Loc.Get("Split_NeedTwoTracks"));
            return;
        }

        StopPreview();

        // This check runs before any of the work, not after it. The cut writes its tracks to a temp
        // folder, and moves the album into Originals/ as its last step. A file the player still holds
        // open would fail at the end of a job that can run for minutes, and the roll-back would throw all
        // of it away. See ReleaseSourceForCutAsync for what StopPreview does not cover.
        if (!await ReleaseSourceForCutAsync())
        {
            SetFinalStatus(Loc.Get("Split_FileInUse"));
            return;
        }

        SetBusy(true);
        _cts = new CancellationTokenSource();
        try
        {
            // The sleeve is needed before the cutting, because the tracks are tagged as they are written.
            // This is best effort. No cover is not a reason to refuse the split.
            if (_chosen is { } chosen)
            {
                StatusText.Text = Loc.Get("Split_StatusCoverArt");
                plan = plan with { Art = await _catalog.SleeveAsync(chosen, _cts.Token) };
            }

            var progress = new Progress<SplitProgress>(p => _dispatcher.TryEnqueue(() =>
                StatusText.Text = Loc.Format("Split_StatusCutting", Math.Min(p.TrackDone + 1, p.TrackTotal), p.TrackTotal)));
            var format = TargetFormat;
            // Only a vinyl session files into folders. An in-place album split stays flat, so the layout
            // is null.
            var layout = _subject.Vinyl is not null ? _layout : null;

            // The splitter reads plan.SourcePath both for what it cuts and for what it moves into
            // Originals/ afterwards. So the restored render is put on the plan, not on the subject.
            string rawPath = plan.SourcePath;
            string cutSource;
            try
            {
                cutSource = await PrepareCutSourceAsync(rawPath, _cts.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed render is not a failed cut. Saying so sends the user to look in the wrong
                // place. Nothing has been written, because RenderAsync deletes its own partial file.
                SetFinalStatus(Loc.Format("Split_RestoreFailed", Faults.Note("split", "applying the clean up", ex).Say()));
                return;
            }
            var cutPlan = plan with { SourcePath = cutSource };

            _result = await Task.Run(() =>
                AlbumFileSplitter.Split(cutPlan, _subject.Current, progress, _cts.Token, targetFormat: format, layout: layout));

            if (!string.Equals(cutSource, rawPath, StringComparison.Ordinal))
                StowRawAlbum(rawPath);

            Hide();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetFinalStatus(Loc.Format("Split_Failed", Faults.Note("split", "splitting the album", ex).Say()));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            _cts?.Cancel();
            return; // first press stops the running work; second closes
        }
        Hide();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusySpinner.Spinning = busy;
        BusyDim.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        RedetectButton.IsEnabled = !busy;
        SplitButton.IsEnabled = !busy;
        UpdateRecognizeEnabled();
        UpdateToolState();
    }
}

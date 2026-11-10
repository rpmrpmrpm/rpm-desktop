using System.Net;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RPM.Core.Migration;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Presentation.Main.Models;
using RPM.Desktop.Services.Migration;
using RPM.Desktop.Services.Shell;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>
/// Imports a library from another music app: choose the app, connect to it (a library file, playlist
/// files, or a server sign-in), see what was found, choose where it goes, follow the progress, read the
/// report.
///
/// <para>The import itself belongs to <see cref="MigrationService"/>, not to this window. Hide closes the
/// window and the import goes on; opening the window again (Add music, From another app) shows it where
/// it is. Every face that shows an import also says that nothing in the other app is changed or
/// removed.</para>
/// </summary>
public sealed partial class MigrationDialog : ContentDialog
{
    private enum Face { Apps, Connect, Reading, Summary, Progress, Report }

    private readonly MigrationService _service;
    private readonly FolderPickerService _picker;
    private readonly DispatcherQueue _dispatcher;

    private Face _face;
    private SourceKind _kind;
    private MigrationConnection? _connection;
    private SourceLibrary? _library;

    /// <summary>The person is signing in again to continue an import that stopped on an expired sign-in.
    /// The import then continues with its own destination, without the "where to" choice: a cloud-only
    /// import must not turn into one to this computer with one click.</summary>
    private bool _signingInAgain;
    private Estimate? _estimate;
    private CancellationTokenSource? _work;
    private IReadOnlyList<PlexServerChoice> _plexServers = [];

    private MigrationDialog(MigrationService service, FolderPickerService picker)
    {
        _service = service;
        _picker = picker;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        InitializeComponent();
        Shell.AttachResize(this, minWidth: 560, minHeight: 440, maxWidth: 860, defaultHeight: 540);

        _service.Changed += OnServiceChanged;
        Closed += (_, _) =>
        {
            _service.Changed -= OnServiceChanged;
            _work?.Cancel();
        };

        // A running or stopped import opens where it is; a finished one shows its report once. After that
        // the window starts at the list of apps, so another import can be started.
        var stopped = _service.LastEnd is not null && _service.LastEnd != RunEnd.Finished;
        var unseenReport = _service.LastEnd == RunEnd.Finished && !_service.ReportSeen;
        if (_service.Current is not null && (_service.IsRunning || stopped || unseenReport))
            ShowRun();
        else
            ShowApps();
    }

    public static async Task ShowAsync(XamlRoot xamlRoot, MigrationService service, FolderPickerService picker)
    {
        var dialog = new MigrationDialog(service, picker) { XamlRoot = xamlRoot };
        await DialogHost.ShowAsync(dialog);
    }

    // ── faces ──

    private void Show(Face face)
    {
        if (face != Face.Connect)
            _finding?.Cancel();
        _face = face;
        AppsFace.Visibility = Vis(face == Face.Apps);
        ConnectFace.Visibility = Vis(face == Face.Connect);
        ReadingFace.Visibility = Vis(face == Face.Reading);
        SummaryFace.Visibility = Vis(face == Face.Summary);
        ProgressFace.Visibility = Vis(face == Face.Progress);
        ReportFace.Visibility = Vis(face == Face.Report);

        BackButton.Visibility = Vis(face is Face.Connect or Face.Summary);
        CloseButton.Visibility = Vis(face != Face.Progress || !_service.IsRunning);
        HideButton.Visibility = Vis(face == Face.Progress && _service.IsRunning);
        PauseButton.Visibility = Vis(face == Face.Progress && _service.IsRunning);
        RetryButton.Visibility = Vis(face == Face.Report && _service.LastReport?.NotImported.Count > 0);
        StartOverButton.Visibility = Visibility.Collapsed;
        PrimaryButton.Visibility = Visibility.Collapsed;
    }

    private void Primary(string key, bool enabled = true)
    {
        PrimaryText.Text = Loc.Get(key);
        PrimaryButton.IsEnabled = enabled;
        PrimaryButton.Visibility = Visibility.Visible;
    }

    private static Visibility Vis(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

    private void ShowApps()
    {
        _signingInAgain = false;
        ContinuePanel.Children.Clear();
        foreach (var open in _service.Unfinished())
        {
            var button = new Button
            {
                Content = Loc.Format("Migration_ContinueImport", Label(open.Kind, open.Name)),
                Style = (Style)Application.Current.Resources["GlassButtonStyle"],
                Padding = new Thickness(16, 0, 16, 0),
                Tag = open,
            };
            button.Click += Continue_Click;
            ContinuePanel.Children.Add(button);
        }
        Show(Face.Apps);
    }

    private void App_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<SourceKind>(tag, out var kind))
            ShowConnect(kind);
    }

    private void ShowConnect(SourceKind kind)
    {
        _kind = kind;
        _connection = null;
        ConnectKicker.Text = Loc.Get(SourceKey(kind));
        FilePanel.Visibility = Vis(kind is SourceKind.MusicBee or SourceKind.ITunes or SourceKind.PlaylistFiles);
        ServerPanel.Visibility = Vis(kind is SourceKind.Jellyfin or SourceKind.Navidrome);
        PlexPanel.Visibility = Vis(kind == SourceKind.Plex);
        QuickConnectButton.Visibility = Vis(kind == SourceKind.Jellyfin);
        PlexServerLabel.Visibility = PlexServerBox.Visibility = Visibility.Collapsed;
        ConnectNote.Visibility = ConnectError.Visibility = Visibility.Collapsed;
        ChosenText.Text = "";

        FileHow.Text = Loc.Get(kind switch
        {
            SourceKind.MusicBee => "Migration_HowMusicBee",
            SourceKind.ITunes => "Migration_HowITunes",
            _ => "Migration_HowPlaylistFiles",
        });
        ChooseFileButton.Content = Loc.Get(kind == SourceKind.PlaylistFiles ? "Migration_ChoosePlaylists" : "Migration_ChooseFile");

        Show(Face.Connect);
        if (kind is SourceKind.Jellyfin or SourceKind.Navidrome)
        {
            Primary("Migration_SignIn");
            _ = FindServersAsync(kind);
        }
    }

    private CancellationTokenSource? _finding;

    /// <summary>Looks for servers of this kind on the network and shows them in the row above the address
    /// box: the turning record while it looks, then the servers found as buttons, or a line saying none
    /// were found. The row keeps its height, so nothing moves.</summary>
    private async Task FindServersAsync(SourceKind kind)
    {
        _finding?.Cancel();
        var finding = _finding = new CancellationTokenSource();
        FoundPanel.Children.Clear();
        FoundPanel.Visibility = Visibility.Collapsed;
        FoundPanel.Opacity = 0;
        FindingRow.Visibility = Visibility.Visible;
        FindingRow.Opacity = 1;
        FindingSpinner.Visibility = Visibility.Visible;
        FindingSpinner.Spinning = true;
        FoundLabel.Text = Loc.Get("Migration_Searching");

        IReadOnlyList<FoundServer> found;
        try
        {
            found = await _service.FindServersAsync(kind, finding.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            FindingSpinner.Spinning = false;
        }
        // The person may have gone to another app or face while the answers came in.
        if (finding.IsCancellationRequested || _face != Face.Connect || _kind != kind)
            return;

        FindingSpinner.Visibility = Visibility.Collapsed;
        if (found.Count == 0)
        {
            FoundLabel.Text = Loc.Get("Migration_NoServersFound");
            return;
        }

        FoundPanel.Children.Add(new TextBlock
        {
            Text = Loc.Get("Migration_FoundServers"),
            Style = (Style)Resources["QuietLineStyle"],
            VerticalAlignment = VerticalAlignment.Center,
        });
        foreach (var server in found)
        {
            var button = new Button
            {
                Content = Loc.Format("Migration_FoundServer", server.Name, server.Address.GetLeftPart(UriPartial.Authority)),
                Style = (Style)Application.Current.Resources["GlassButtonStyle"],
                Padding = new Thickness(14, 0, 14, 0),
                Tag = server,
            };
            button.Click += FoundServer_Click;
            FoundPanel.Children.Add(button);
        }
        // The servers fade in over the searching line, in the same place.
        FindingRow.Visibility = Visibility.Collapsed;
        FoundPanel.Visibility = Visibility.Visible;
        Fade(FoundPanel);
    }

    private static void Fade(UIElement element)
    {
        var fade = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
        };
        var story = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fade, element);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fade, "Opacity");
        story.Children.Add(fade);
        story.Completed += (_, _) => element.Opacity = 1;
        story.Begin();
    }

    private void FoundServer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FoundServer server })
            return;
        AddressBox.Text = server.Address.GetLeftPart(UriPartial.Authority);
        UserBox.Focus(FocusState.Programmatic);
    }

    // ── connect ──

    private async void ChooseFile_Click(object sender, RoutedEventArgs e)
    {
        var playlists = _kind == SourceKind.PlaylistFiles;
        var files = await _picker.PickFilesAsync(playlists ? [".m3u", ".m3u8", ".pls"] : [".xml"], multiple: playlists);
        if (files.Count == 0)
            return;
        ChosenText.Text = files.Count == 1 ? files[0] : string.Join(", ", files.Select(Path.GetFileName));
        await ReadAsync(new MigrationConnection(_kind, files));
    }

    private async void QuickConnect_Click(object sender, RoutedEventArgs e)
    {
        if (ServerAddress() is not { } server)
            return;
        Cancel();
        var ct = _work!.Token;
        try
        {
            using var http = MigrationService.HttpFor(server);
            var (code, secret) = await JellyfinSignIn.StartQuickConnectAsync(http, server, ct);
            Note(Loc.Format("Migration_QuickConnectCode", code));
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
                if (await JellyfinSignIn.FinishQuickConnectAsync(http, server, secret, ct) is { } session)
                {
                    await ReadAsync(new MigrationConnection(SourceKind.Jellyfin, [], Jellyfin: session));
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error(ex); }
    }

    private async void PlexSignIn_Click(object sender, RoutedEventArgs e)
    {
        Cancel();
        var ct = _work!.Token;
        try
        {
            using var http = MigrationService.PlexTv();
            var clientId = _service.PlexClientId;
            var pin = await PlexSignIn.StartAsync(http, clientId, ct);
            await Windows.System.Launcher.LaunchUriAsync(pin.AuthPage);
            Note(Loc.Get("Migration_PlexWaiting"));

            string? token = null;
            while (token is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                token = await PlexSignIn.FinishAsync(http, clientId, pin, ct);
            }
            _plexServers = await PlexSignIn.ServersAsync(http, clientId, token, ct);
            if (_plexServers.Count == 0)
            {
                Error(Loc.Get("Migration_PlexNoServer"));
                return;
            }
            ConnectNote.Visibility = Visibility.Collapsed;
            if (_plexServers.Count == 1)
            {
                await ConnectPlexAsync(_plexServers[0], ct);
                return;
            }
            // Names only: a list of records would be bound by reflection, which the published (AOT) build
            // cannot do. The index maps back to the server.
            PlexServerBox.ItemsSource = _plexServers.Select(s => s.Name).ToList();
            PlexServerBox.SelectedIndex = 0;
            PlexServerLabel.Visibility = PlexServerBox.Visibility = Visibility.Visible;
            Primary("Migration_Continue");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error(ex); }
    }

    private async Task ConnectPlexAsync(PlexServerChoice server, CancellationToken ct)
    {
        using var http = MigrationService.PlexTv();
        if (await PlexSignIn.PickConnectionAsync(http, server, ct) is not { } connection)
        {
            Error(Loc.Get("Migration_PlexNoServer"));
            return;
        }
        await ReadAsync(new MigrationConnection(SourceKind.Plex, [], Plex: server, PlexConnection: connection));
    }

    private async Task SignInAsync()
    {
        if (ServerAddress() is not { } server)
            return;
        Cancel();
        var ct = _work!.Token;
        // The reading face with its moving bar from the first moment: a server far away can take a while to
        // answer, and a button that only greys out says nothing.
        ReadingText.Text = Loc.Get("Migration_SigningIn");
        Show(Face.Reading);
        try
        {
            using var http = MigrationService.HttpFor(server);
            MigrationConnection connection;
            if (_kind == SourceKind.Jellyfin)
            {
                var session = await JellyfinSignIn.WithPasswordAsync(http, server, UserBox.Text.Trim(), PasswordBox.Password, ct);
                connection = new MigrationConnection(SourceKind.Jellyfin, [], Jellyfin: session);
            }
            else
            {
                var login = new SubsonicLogin(server, UserBox.Text.Trim(), PasswordBox.Password);
                await SubsonicCheck.CheckAsync(http, login, ct);
                connection = new MigrationConnection(SourceKind.Navidrome, [], Subsonic: login);
            }
            await ReadAsync(connection);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Back to the filled-in form, with the reason; the servers found stay listed.
            Show(Face.Connect);
            Primary("Migration_SignIn");
            Error(ex);
        }
    }

    private Uri? ServerAddress()
    {
        var text = AddressBox.Text.Trim();
        if (text.Length > 0 && !text.Contains("://", StringComparison.Ordinal))
            text = "http://" + text;
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            return uri;
        Error(Loc.Get("Migration_ServerUnreachable"));
        return null;
    }

    private void Note(string text)
    {
        ConnectNote.Text = text;
        ConnectNote.Visibility = Visibility.Visible;
        ConnectError.Visibility = Visibility.Collapsed;
    }

    private void Error(string text)
    {
        ConnectError.Text = text;
        ConnectError.Visibility = Visibility.Visible;
    }

    private void Error(Exception ex) => Error(Loc.Get(ex switch
    {
        MediaServerSignInFailed or MediaServerSignInExpired => "Migration_SignInFailed",
        FormatException or System.Xml.XmlException => "Migration_FileUnreadable",
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => "Migration_SignInFailed",
        _ when _kind is SourceKind.MusicBee or SourceKind.ITunes or SourceKind.PlaylistFiles => "Migration_FileUnreadable",
        _ => "Migration_ServerUnreachable",
    }));

    // ── read, summary, start ──

    private async Task ReadAsync(MigrationConnection connection)
    {
        Cancel();
        var ct = _work!.Token;
        _connection = connection;
        ReadingText.Text = Loc.Format("Migration_Reading", Count(0));
        Show(Face.Reading);
        try
        {
            var found = new Progress<int>(n => ReadingText.Text = Loc.Format("Migration_Reading", Count(n)));
            _library = await _service.ReadAsync(connection, found, ct);
            var library = _library;
            if (_signingInAgain && _service.Current is { } stopped && stopped.Kind == library.Kind && stopped.LibraryId == library.LibraryId)
            {
                _signingInAgain = false;
                if (_service.Start(library, connection, _service.LastDestination, _service.CurrentLabel, ImportStart.Continue))
                    ShowRun();
                return;
            }
            _estimate = await Task.Run(() => _service.Estimate(library), ct);
            await ShowSummaryAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ShowConnect(connection.Kind);
            Error(ex);
        }
    }

    private async Task ShowSummaryAsync()
    {
        if (_library is not { } library || _estimate is not { } estimate)
            return;

        SummaryTracks.Text = library.Tracks.Count == 1
            ? Loc.Format("Migration_SummaryTracksOne", Size(estimate.TotalBytes))
            : Loc.Format("Migration_SummaryTracks", Count(library.Tracks.Count), Size(estimate.TotalBytes));
        SummaryPlaylists.Text = library.Playlists.Count == 1
            ? Loc.Get("Migration_SummaryPlaylistsOne")
            : Loc.Format("Migration_SummaryPlaylists", Count(library.Playlists.Count));
        SummaryPlaylists.Visibility = Vis(library.Playlists.Count > 0);
        SummaryAlready.Text = Loc.Format("Migration_SummaryAlready", Count(estimate.AboutAlreadyInRpm));
        SummaryAlready.Visibility = Vis(estimate.AboutAlreadyInRpm > 0);
        SummarySkipped.Text = Loc.Format("Migration_SummarySkipped", Count(library.Skipped.Count));
        SummarySkipped.Visibility = Vis(library.Skipped.Count > 0);
        SummaryKeeps.Text = Keeps(library);

        var server = library.Kind is SourceKind.Jellyfin or SourceKind.Plex or SourceKind.Navidrome;
        WherePanel.Visibility = Vis(server);
        ToCloud.IsEnabled = _service.CloudReady;
        ToCloudHint.Text = Loc.Get(_service.CloudReady ? "Migration_ToCloudHint" : "Migration_CloudSignInNeeded");

        // An earlier import of this library that did not finish: the person chooses to continue it, with
        // its own destination, or to start over.
        var unfinished = _service.UnfinishedFor(library);
        if (unfinished is not null)
        {
            var counts = unfinished.Counts();
            UnfinishedText.Text = Loc.Format("Migration_UnfinishedNote",
                Count(counts.Imported + counts.AlreadyInRpm + counts.NotImported), Count(counts.Found));
            (unfinished.Destination == MigrationDestination.CloudOnly && _service.CloudReady ? ToCloud : ToComputer).IsChecked = true;
        }
        UnfinishedText.Visibility = Vis(unfinished is not null);
        if (!_service.CloudReady)
            ToComputer.IsChecked = true;

        Show(Face.Summary);
        await RenderSpaceAsync();
    }

    private async void Where_Changed(object sender, RoutedEventArgs e)
    {
        if (_face == Face.Summary)
            await RenderSpaceAsync();
    }

    private MigrationDestination Destination => ToCloud.IsChecked == true && ToCloud.IsEnabled
        ? MigrationDestination.CloudOnly
        : MigrationDestination.ThisComputer;

    private async Task RenderSpaceAsync()
    {
        if (_estimate is not { } estimate)
            return;
        var needed = estimate.AboutBytesNeeded;
        var startable = true;
        SpaceText.Style = (Style)Resources["QuietLineStyle"];

        if (Destination == MigrationDestination.ThisComputer)
        {
            if (_service.FreeDiskBytes() is { } free)
            {
                startable = free >= needed;
                SpaceText.Text = Loc.Format(startable ? "Migration_SpaceDisk" : "Migration_SpaceDiskShort",
                    Size(needed), Size(free));
                if (!startable)
                    SpaceText.Style = (Style)Resources["WarnLineStyle"];
            }
            else
            {
                SpaceText.Text = "";
            }
        }
        else
        {
            SpaceText.Text = "";
            if (await _service.CloudSpaceAsync(CancellationToken.None) is { Unlimited: false } space)
            {
                var free = Math.Max(0, space.Quota - space.Used);
                SpaceText.Text = Loc.Format(free >= needed ? "Migration_SpaceCloud" : "Migration_SpaceCloudShort",
                    Size(needed), Size(free));
            }
        }
        var unfinished = _library is { } shown && _service.UnfinishedFor(shown) is not null;
        Primary(unfinished ? "Migration_ContinueStopped" : "Migration_Start", startable);
        StartOverButton.Visibility = Vis(unfinished);
        StartOverButton.IsEnabled = startable;
    }

    private void Start(ImportStart how)
    {
        if (_library is not { } library || _connection is not { } connection)
            return;
        if (_service.Start(library, connection, Destination, Label(library.Kind, library.Name), how))
            ShowRun();
    }

    private void StartOver_Click(object sender, RoutedEventArgs e) => Start(ImportStart.StartOver);

    private async void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MigrationSummary open })
            return;
        if (_service.SavedConnection(open) is not { } connection)
        {
            ShowConnect(open.Kind);
            return;
        }

        Cancel();
        var ct = _work!.Token;
        _kind = open.Kind;
        Show(Face.Reading);
        try
        {
            var library = await _service.ReadAsync(connection, new Progress<int>(n => ReadingText.Text = Loc.Format("Migration_Reading", Count(n))), ct);
            if (_service.Start(library, connection, open.Destination, Label(library.Kind, library.Name), ImportStart.Continue))
                ShowRun();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ShowConnect(open.Kind);
            Error(ex);
        }
    }

    // ── the run ──

    // At most one redraw waits on the UI thread. A fast import reports after every track; the redraw reads
    // the service's latest state when it runs, so the reports in between are not needed.
    private int _redrawQueued;

    private void OnServiceChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _redrawQueued, 1) == 1)
            return;
        if (!_dispatcher.TryEnqueue(Redraw))
            Interlocked.Exchange(ref _redrawQueued, 0);
    }

    private void Redraw()
    {
        Interlocked.Exchange(ref _redrawQueued, 0);
        if (_face is Face.Progress or Face.Report)
            ShowRun();
    }

    private void ShowRun()
    {
        if (_service.LastEnd == RunEnd.Finished && _service.LastReport is { } report)
        {
            ShowReport(report);
            return;
        }

        Show(Face.Progress);
        var p = _service.Progress;
        var library = _service.Current;
        StageText.Text = Loc.Get(p?.Stage switch
        {
            MigrationStage.Playlists => "Migration_StagePlaylists",
            MigrationStage.Finishing => "Migration_StageFinishing",
            MigrationStage.Reading => "Migration_StageReading",
            MigrationStage.Waiting => "Migration_StageWaiting",
            _ => "Migration_StageTracks",
        });

        if (p is not null)
        {
            var done = p.Counts.Imported + p.Counts.AlreadyInRpm + p.Counts.NotImported;
            Bar.Value = p.BytesTotal > 0 ? (double)p.BytesDone / p.BytesTotal : p.Total > 0 ? (double)done / p.Total : 0;
            ProgressTracks.Text = Loc.Format("Migration_ProgressTracks", Count(done), Count(p.Total), Size(p.BytesDone), Size(p.BytesTotal));
            ProgressCounts.Text = Loc.Format("Migration_ProgressCounts", Count(p.Counts.Imported), Count(p.Counts.AlreadyInRpm), Count(p.Counts.NotImported));
            ProgressAlbum.Text = p.CurrentAlbum is { Length: > 0 } album ? Loc.Format("Migration_ProgressAlbum", album) : "";
            // An estimate only means something while the import runs.
            ProgressTimeLeft.Text = !_service.IsRunning ? "" : p.TimeLeft switch
            {
                null => "",
                { TotalMinutes: < 1 } => Loc.Get("Migration_ProgressUnderAMinute"),
                { } left => Loc.Format("Migration_ProgressTimeLeft", PaneStats.Duration(left.TotalSeconds)),
            };
        }
        ProgressKeeps.Text = library is null ? "" : Keeps(library);

        var stopped = _service.LastEnd;
        ProgressStopped.Visibility = Vis(stopped is not null && stopped != RunEnd.Finished);
        ProgressStopped.Text = Loc.Get(stopped switch
        {
            RunEnd.SignInExpired => "Migration_StoppedSignIn",
            RunEnd.DiskFull => "Migration_StoppedDiskFull",
            RunEnd.CloudFull => "Migration_StoppedCloudFull",
            RunEnd.CloudOff => "Migration_StoppedCloudOff",
            RunEnd.Failed => "Migration_StoppedError",
            _ => "Migration_Paused",
        });
        if (!_service.IsRunning && stopped is not null)
            Primary("Migration_Continue");
    }

    private void ShowReport(MigrationReport report)
    {
        Show(Face.Report);
        _service.ReportSeen = true;
        ReportTitle.Text = Loc.Format("Migration_ReportTitle", _service.CurrentLabel);
        ReportCounts.Text = Loc.Format("Migration_ReportCounts", Count(report.Counts.Found), Count(report.Counts.Imported), Count(report.Counts.AlreadyInRpm), Count(report.Counts.NotImported));
        ReportPlaylists.Text = report.Playlists.Count == 1
            ? Loc.Get("Migration_ReportPlaylistsOne")
            : Loc.Format("Migration_ReportPlaylists", Count(report.Playlists.Count));
        ReportPlaylists.Visibility = Vis(report.Playlists.Count > 0);

        var notes = report.Playlists.Where(p => p.Unmatched > 0 || p.WasSmart)
            .Select(playlist => playlist.WasSmart && playlist.Unmatched == 0
                ? Loc.Format("Migration_ReportSmart", playlist.Name)
                : playlist.Unmatched == 1
                    ? Loc.Format("Migration_ReportPlaylistUnmatchedOne", playlist.Name)
                    : Loc.Format("Migration_ReportPlaylistUnmatched", playlist.Name, Count(playlist.Unmatched)))
            .ToList();
        ReportPlaylistNotes.ItemsSource = notes;
        ReportPlaylistNotes.Visibility = Vis(notes.Count > 0);

        NotImportedPanel.Visibility = Vis(report.NotImported.Count > 0);
        NotImportedList.ItemsSource = report.NotImported
            .Select(n => $"{n.What}: {Loc.Get(n.ReasonKey)}{(n.Detail is { Length: > 0 } d ? $" ({d})" : "")}")
            .ToList();
        LowerQualityPanel.Visibility = Vis(report.LowerQualityInRpm.Count > 0);
        LowerQualityList.ItemsSource = report.LowerQualityInRpm.ToList();
        SkippedPanel.Visibility = Vis(report.Skipped.Count > 0);
        SkippedList.ItemsSource = report.Skipped.Select(s => $"{s.What}: {Loc.Get(s.ReasonKey)}").ToList();
    }

    // ── buttons ──

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        switch (_face)
        {
            case Face.Connect when _kind is SourceKind.Jellyfin or SourceKind.Navidrome:
                await SignInAsync();
                break;
            case Face.Connect when _kind == SourceKind.Plex && PlexServerBox.SelectedIndex >= 0 && PlexServerBox.SelectedIndex < _plexServers.Count:
                Cancel();
                try { await ConnectPlexAsync(_plexServers[PlexServerBox.SelectedIndex], _work!.Token); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Error(ex); }
                break;
            case Face.Summary:
                Start(ImportStart.Continue);
                break;
            case Face.Progress when !_service.IsRunning:
                await ContinueRunAsync();
                break;
        }
    }

    /// <summary>Continue after a pause or a stop: a sign-in that expired asks for the sign-in again;
    /// anything else starts the same import again, which picks up where it stopped.</summary>
    private async Task ContinueRunAsync()
    {
        if (_service.Current is not { } library || _service.LastConnection is not { } connection)
            return;
        if (_service.LastEnd == RunEnd.SignInExpired)
        {
            _signingInAgain = true;
            ShowConnect(library.Kind);
            return;
        }
        Cancel();
        try
        {
            var fresh = connection.Kind is SourceKind.Jellyfin or SourceKind.Plex or SourceKind.Navidrome
                ? await _service.ReadAsync(connection, new Progress<int>(), _work!.Token)
                : library;
            _service.Start(fresh, connection, _service.LastDestination, _service.CurrentLabel, ImportStart.Continue);
            ShowRun();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ShowConnect(library.Kind);
            Error(ex);
        }
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_service.Current is not { } library || _service.LastConnection is not { } connection || _service.LastReport is not { } report)
            return;
        _service.Retry(library, _service.LastDestination, report.NotImported.Select(n => n.SourceId));
        if (_service.Start(library, connection, _service.LastDestination, _service.CurrentLabel, ImportStart.Continue))
            ShowRun();
    }

    private void Pause_Click(object sender, RoutedEventArgs e) => _service.Pause();

    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        Cancel();
        if (_face == Face.Summary)
            ShowConnect(_kind);
        else
            ShowApps();
    }

    private void Cancel()
    {
        _work?.Cancel();
        _work = new CancellationTokenSource();
    }

    // ── words ──

    /// <summary>A size as the rest of the app writes it, and "0 KB" for nothing yet (the shared helper
    /// writes nothing for zero, which reads as a gap in a progress line).</summary>
    /// <summary>A count as people read it, with the separators of the system's number format ("24,993").</summary>
    private static string Count(int n) => n.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);

    private static string Size(long bytes) => bytes > 0 ? TrackFieldText.Size(bytes) : Loc.Format("Unit_KB", 0);

    private static string SourceKey(SourceKind kind) => kind switch
    {
        SourceKind.MusicBee => "Migration_SourceMusicBee",
        SourceKind.ITunes => "Migration_SourceITunes",
        SourceKind.Jellyfin => "Migration_SourceJellyfin",
        SourceKind.Plex => "Migration_SourcePlex",
        SourceKind.Navidrome => "Migration_SourceNavidrome",
        _ => "Migration_SourcePlaylistFiles",
    };

    /// <summary>The source as the windows name it: the library's own name (a server's name), else the
    /// app's name; playlist files have no name of their own.</summary>
    private static string Label(SourceKind kind, string name) =>
        kind != SourceKind.PlaylistFiles && name.Length > 0 ? name : Loc.Get(SourceKey(kind));

    private static string Keeps(SourceLibrary library) => library.Kind switch
    {
        SourceKind.PlaylistFiles => Loc.Get("Migration_KeepsOriginalsPlaylists"),
        SourceKind.MusicBee or SourceKind.ITunes => Loc.Format("Migration_KeepsOriginalsFile", Label(library.Kind, library.Name)),
        _ => Loc.Format("Migration_KeepsOriginalsServer", Label(library.Kind, library.Name)),
    };
}

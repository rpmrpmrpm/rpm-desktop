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

        if (_service.IsRunning || _service.LastEnd is not null && _service.Current is not null)
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
            Primary("Migration_SignIn");
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
        PrimaryButton.IsEnabled = false;
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
        catch (Exception ex) { Error(ex); }
        finally { PrimaryButton.IsEnabled = true; }
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
        ReadingText.Text = Loc.Format("Migration_Reading", 0);
        Show(Face.Reading);
        try
        {
            var found = new Progress<int>(n => ReadingText.Text = Loc.Format("Migration_Reading", n));
            _library = await _service.ReadAsync(connection, found, ct);
            var library = _library;
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

        SummaryTracks.Text = Loc.Format("Migration_SummaryTracks", library.Tracks.Count, TrackFieldText.Size(estimate.TotalBytes));
        SummaryPlaylists.Text = Loc.Format("Migration_SummaryPlaylists", library.Playlists.Count);
        SummaryAlready.Text = Loc.Format("Migration_SummaryAlready", estimate.AboutAlreadyInRpm);
        SummarySkipped.Text = Loc.Format("Migration_SummarySkipped", library.Skipped.Count);
        SummarySkipped.Visibility = Vis(library.Skipped.Count > 0);
        SummaryKeeps.Text = Keeps(library);

        var server = library.Kind is SourceKind.Jellyfin or SourceKind.Plex or SourceKind.Navidrome;
        WherePanel.Visibility = Vis(server);
        ToCloud.IsEnabled = _service.CloudReady;
        ToCloudHint.Text = Loc.Get(_service.CloudReady ? "Migration_ToCloudHint" : "Migration_CloudSignInNeeded");
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
                    TrackFieldText.Size(needed), TrackFieldText.Size(free));
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
                    TrackFieldText.Size(needed), TrackFieldText.Size(free));
            }
        }
        Primary("Migration_Start", startable);
    }

    private void Start()
    {
        if (_library is not { } library || _connection is not { } connection)
            return;
        if (_service.Start(library, connection, Destination, Label(library.Kind, library.Name)))
            ShowRun();
    }

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
            var library = await _service.ReadAsync(connection, new Progress<int>(n => ReadingText.Text = Loc.Format("Migration_Reading", n)), ct);
            if (_service.Start(library, connection, open.Destination, Label(library.Kind, library.Name)))
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

    private void OnServiceChanged(object? sender, EventArgs e) => _dispatcher.TryEnqueue(() =>
    {
        if (_face is Face.Progress or Face.Report)
            ShowRun();
    });

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
            _ => "Migration_StageTracks",
        });

        if (p is not null)
        {
            var done = p.Counts.Imported + p.Counts.AlreadyInRpm + p.Counts.NotImported;
            Bar.Value = p.BytesTotal > 0 ? (double)p.BytesDone / p.BytesTotal : p.Total > 0 ? (double)done / p.Total : 0;
            ProgressTracks.Text = Loc.Format("Migration_ProgressTracks", done, p.Total, TrackFieldText.Size(p.BytesDone), TrackFieldText.Size(p.BytesTotal));
            ProgressCounts.Text = Loc.Format("Migration_ProgressCounts", p.Counts.Imported, p.Counts.AlreadyInRpm, p.Counts.NotImported);
            ProgressAlbum.Text = p.CurrentAlbum is { Length: > 0 } album ? Loc.Format("Migration_ProgressAlbum", album) : "";
            ProgressTimeLeft.Text = p.TimeLeft is { } left ? Loc.Format("Migration_ProgressTimeLeft", PaneStats.Duration(left.TotalSeconds)) : "";
        }
        ProgressKeeps.Text = library is null ? "" : Keeps(library);

        var stopped = _service.LastEnd;
        ProgressStopped.Visibility = Vis(stopped is not null && stopped != RunEnd.Finished);
        ProgressStopped.Text = Loc.Get(stopped switch
        {
            RunEnd.SignInExpired => "Migration_StoppedSignIn",
            RunEnd.DiskFull => "Migration_StoppedDiskFull",
            RunEnd.CloudFull => "Migration_StoppedCloudFull",
            _ => "Migration_Paused",
        });
        if (!_service.IsRunning && stopped is not null)
            Primary("Migration_Continue");
    }

    private void ShowReport(MigrationReport report)
    {
        Show(Face.Report);
        ReportTitle.Text = Loc.Format("Migration_ReportTitle", _service.CurrentLabel);
        ReportCounts.Text = Loc.Format("Migration_ReportCounts", report.Counts.Found, report.Counts.Imported, report.Counts.AlreadyInRpm, report.Counts.NotImported);
        ReportPlaylists.Text = Loc.Format("Migration_ReportPlaylists", report.Playlists.Count);
        ReportPlaylists.Visibility = Vis(report.Playlists.Count > 0);

        ReportPlaylistNotes.Children.Clear();
        foreach (var playlist in report.Playlists.Where(p => p.Unmatched > 0 || p.WasSmart))
        {
            ReportPlaylistNotes.Children.Add(new TextBlock
            {
                Text = playlist.WasSmart && playlist.Unmatched == 0
                    ? Loc.Format("Migration_ReportSmart", playlist.Name)
                    : Loc.Format("Migration_ReportPlaylistUnmatched", playlist.Name, playlist.Unmatched),
                Style = (Style)Resources["QuietLineStyle"],
            });
        }

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
                Start();
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
            ShowConnect(library.Kind);
            return;
        }
        Cancel();
        try
        {
            var fresh = connection.Kind is SourceKind.Jellyfin or SourceKind.Plex or SourceKind.Navidrome
                ? await _service.ReadAsync(connection, new Progress<int>(), _work!.Token)
                : library;
            _service.Start(fresh, connection, _service.LastDestination, _service.CurrentLabel);
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
        if (_service.Start(library, connection, _service.LastDestination, _service.CurrentLabel))
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

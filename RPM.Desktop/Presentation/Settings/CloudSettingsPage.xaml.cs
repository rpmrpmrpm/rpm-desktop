using RPM.Core.Cloud;
using RPM.Desktop.Presentation.Dialogs;
using RPM.Desktop.Services;
using RPM.Desktop.Strings;
using Microsoft.Extensions.DependencyInjection;

namespace RPM.Desktop.Presentation.Settings;

/// <summary>
/// The cloud's controls: how much of the library goes up, whether the local copy comes back down,
/// and what the uploader is doing about it.
/// </summary>
public sealed partial class CloudSettingsPage : Page
{
    private readonly SettingsService _settings;
    private readonly CloudSyncService _cloud;
    private readonly AuthService _auth;
    private readonly LibraryService _library;
    private readonly CloudJobService _jobs;
    private readonly SupporterService _supporter;

    /// <summary>Set while the controls are filled in from the settings, so that doing so does not count
    /// as the user changing them.</summary>
    private bool _loading;

    public CloudSettingsPage(IServiceProvider services)
    {
        _settings = services.GetRequiredService<SettingsService>();
        _cloud = services.GetRequiredService<CloudSyncService>();
        _auth = services.GetRequiredService<AuthService>();
        _library = services.GetRequiredService<LibraryService>();
        _jobs = services.GetRequiredService<CloudJobService>();
        _supporter = services.GetRequiredService<SupporterService>();

        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _cloud.Changed += OnCloudChanged;
        _auth.Changed += OnAuthChanged;
        _jobs.Changed += OnJobsChanged;
        _supporter.Changed += OnSupporterChanged;

        // The mode can change while this page is open, without anyone touching it. A full store moves
        // "all music" to the selection, and buying storage restores it. The radio must follow, or the
        // page shows a setting that is no longer the real one.
        _settings.Changed += OnSettingsChanged;

        Load();
        LoadHistory();
        LoadErrors();
        _ = _cloud.KickAsync();

        // The page is painted from the last known state, then asked again. It opens with an answer, not
        // a blank line that fills in a second later.
        RenderSupporter();
        _ = _supporter.GetAsync();

        ShowPretend();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _cloud.Changed -= OnCloudChanged;
        _auth.Changed -= OnAuthChanged;
        _jobs.Changed -= OnJobsChanged;
        _supporter.Changed -= OnSupporterChanged;
        _settings.Changed -= OnSettingsChanged;
    }

    /// <summary>Any setting. The radio is re-read from what is stored. This is cheap, and without it
    /// the page would disagree with itself.</summary>
    private void OnSettingsChanged() => DispatcherQueue.TryEnqueue(Load);

    private void Load()
    {
        _loading = true;

        SignInPanel.Visibility = _auth.IsLoggedIn ? Visibility.Collapsed : Visibility.Visible;
        CloudPanel.Opacity = _auth.IsLoggedIn ? 1 : 0.4;
        CloudPanel.IsHitTestVisible = _auth.IsLoggedIn;

        (_settings.CloudMode switch
        {
            CloudSyncMode.All => ModeAll,
            CloudSyncMode.Selected => ModeSelected,
            _ => ModeNone,
        }).IsChecked = true;

        _loading = false;
        Render(_cloud.Status);
        RenderOffload();
    }

    private void OnCloudChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        Render(_cloud.Status);

        // A bring-back changes these numbers while it runs, so the page follows it. It does not show
        // what was true when the page was opened.
        RenderOffload();
    });

    /// <summary>A track finishing is when a new log line exists. So the history is re-read then, and
    /// not only when the page opens.</summary>
    private void OnJobsChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        LoadHistory();
        LoadErrors();
    });

    private void OnAuthChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        Load();
        RenderSupporter();
        _ = _supporter.GetAsync();
    });

    private void OnSupporterChanged() => DispatcherQueue.TryEnqueue(RenderSupporter);

    /// <summary>
    /// What this account's storage is, and whether there is anything to buy.
    ///
    /// <para>There are three states, not two: a supporter, someone on the free 100 GB, and an account
    /// the server would not answer about. The third is never shown as the second. Telling someone who
    /// pays that they do not, because a request timed out, is the one mistake here that matters. So an
    /// unknown state says so and offers nothing.</para>
    /// </summary>
    private void RenderSupporter()
    {
        var status = _supporter.Current;

#if DEBUG
        // While something is being pretended, the card shows the pretence. It is the standing the rest
        // of the app acts on. A card still showing the real account would make the switch above look as
        // if it did nothing.
        if (_supporter.Pretend is not null)
            status = _supporter.Standing;
#endif

        SupporterText.Text = status switch
        {
            { IsSupporter: true, Until: { } until } =>
                Loc.Format("Cloud_SupporterYesUntil", until.LocalDateTime.ToString("d")),
            { IsSupporter: true } => Loc.Get("Cloud_SupporterYes"),
            { IsSupporter: false } => Loc.Get("Cloud_SupporterFree"),
            _ => Loc.Get("Cloud_SupporterUnknown"),
        };

        // The badge is not dressed here. It reads the standing itself and follows it. That lets it sit
        // beside a name on any screen without that screen knowing about supporters. See SupporterBadge.

        // Offered only to an account known not to be a supporter.
        SupportButton.Visibility = status is { IsSupporter: false } ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnBecomeSupporter(object sender, RoutedEventArgs e) => SupporterLinks.OpenBecomeSupporter();

    // Debug only: pretend a standing

    /// <summary>Set while the radio buttons are being set from the stored choice, so setting a mark in
    /// one is not read as the user clicking it. Debug builds only, like every use of it.</summary>
#if DEBUG
    private bool _dressingPretend;
#endif

    /// <summary>Shows the card in a debug build, with the stored choice set on it. In a release build
    /// this does nothing, and the card stays collapsed. The card is collapsed in the markup, so release
    /// builds never show it.</summary>
    private void ShowPretend()
    {
#if DEBUG
        SupporterDebugCard.Visibility = Visibility.Visible;

        _dressingPretend = true;
        try
        {
            PretendSupporter.IsChecked = _supporter.Pretend is true;
            PretendFree.IsChecked = _supporter.Pretend is false;
            PretendReal.IsChecked = _supporter.Pretend is null;
        }
        finally
        {
            _dressingPretend = false;
        }
#endif
    }

    private void OnPretendChanged(object sender, RoutedEventArgs e)
    {
#if DEBUG
        if (_dressingPretend)
            return;

        // The service stores the value and raises the change. So this handler does not touch settings
        // or repaint. The change it raises comes back through OnSupporterChanged, like any real answer.
        _supporter.Pretend =
            PretendSupporter.IsChecked is true ? true
            : PretendFree.IsChecked is true ? false
            : null;
#endif
    }

    private void Render(CloudSyncStatus status)
    {
        var space = status.Space;

        // The number, and what it means. "84 GB of 100 GB" is a fact that nobody reads as a warning
        // until it is one, so the state is shown on the same line as the size. A quota of zero is a
        // read that did not come back. It is not an account with no room. Showing "0 B of 0 B" would
        // state a fact about the account that nobody has confirmed.
        UsageText.Text = space switch
        {
            { Unlimited: true } => Loc.Format("Cloud_UsageUnlimited", ByteSize.Human(space.UsedBytes)),
            { QuotaBytes: <= 0 } => Loc.Get("Rpm_Profile_StorageUnknown"),
            { IsFull: true } => Loc.Format("Cloud_UsageFull",
                ByteSize.Human(space.UsedBytes), ByteSize.Human(space.QuotaBytes)),
            { IsAlmostFull: true } => Loc.Format("Cloud_UsageAlmostFull",
                ByteSize.Human(space.UsedBytes), ByteSize.Human(space.QuotaBytes)),
            _ => Loc.Format("Cloud_Usage",
                ByteSize.Human(space.UsedBytes), ByteSize.Human(space.QuotaBytes)),
        };
        UsageBar.Value = status.UsedFraction;

        // Counted from the mirror, not from the account. What is in the cloud is what this library says
        // it put there. This is the same record that the modes above are read against.
        UsageTracksText.Text = _library.IsOpen
            ? Loc.Format("Rpm_Profile_StorageTracks",
                _library.Db.CountByCloudState(CloudState.Synced)
                + _library.Db.CountByCloudState(CloudState.CloudOnly))
            : "";

        // The warning appears while there is still room to act on it. It is not shown when the store is
        // full, because the line above already says so, and the status line below says what was done
        // about it.
        AlmostFullText.Visibility = space.IsNearlyFull ? Visibility.Visible : Visibility.Collapsed;
        if (space.IsNearlyFull)
            AlmostFullText.Text = Loc.Format("Cloud_AlmostFullHint", ByteSize.Human(CloudSpace.AlmostFullBytes));

        // Shown while the mode on screen is the one the full store moved it to. This holds even if the
        // store still has no room. The setting stays where it was put until support is bought.
        SyncAllPausedText.Visibility = _settings.SyncAllPausedByFull && !space.Unlimited
            ? Visibility.Visible
            : Visibility.Collapsed;


        ProgressPanel.Visibility = status.Running ? Visibility.Visible : Visibility.Collapsed;
        // Collapsed is not enough. An indeterminate bar keeps its Forever storyboard running, which
        // pins the compositor at full frame rate while the page is open. So the sweep is turned off
        // with the panel.
        BusyBar.IsIndeterminate = status.Running;
        ProgressText.Text = status.CurrentTitle is { Length: > 0 } title
            ? Loc.Format("Cloud_Uploading", title)
            : "";

        var parts = new List<string>();
        if (status.Pending > 0) parts.Add(Loc.Format("Cloud_Pending", status.Pending));
        if (status.Failed > 0) parts.Add(Loc.Format("Cloud_Failed", status.Failed));

        StatusText.Text =
            status.LastError is { } trouble ? FaultText.Format(trouble.StringKey, trouble.Args)
            : (parts.Count > 0
                ? string.Join(" · ", parts)
                : _settings.CloudMode == CloudSyncMode.None ? "" : Loc.Get("Cloud_UpToDate"));

        RetryButton.Visibility = status.Failed > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string tag }) return;

        // The user has changed the mode. What the full store did to it is not undone later. A mode the
        // user chose is theirs, and nothing may put it back on their behalf.
        _settings.SyncAllPausedByFull = false;

        _settings.CloudMode = tag switch
        {
            "All" => CloudSyncMode.All,
            "Selected" => CloudSyncMode.Selected,
            _ => CloudSyncMode.None,
        };

        Render(_cloud.Status);
    }

    /// <summary>What of this library is in the cloud and not on this disk. Two numbers describe it:
    /// what this machine had and gave up, and what it never had.
    ///
    /// <para>rpm removes no files of its own. So the first number is the user's own deletion, read back
    /// to them. The tracks are still in the library, still on the wall, and still playable. The only
    /// thing that changed is a number on a disk the user is not looking at.</para>
    /// </summary>
    private void RenderOffload()
    {
        var gone = _cloud.OffloadedHere();
        var elsewhere = _cloud.FromElsewhere();

        // Strictly what is missing from THIS machine. The line must not say that every track in the
        // library is on this computer. That is a claim about the library, and it is false for anyone
        // with a second install. Their other machine's records are in this library, on the wall,
        // playing, and were never on this disk. That is the line below. It is a separate fact with its
        // own number.
        OffloadStateText.Text = gone.Any
            ? Loc.Format("Cloud_OffloadState", gone.Count, ByteSize.Human(gone.Bytes))
            : Loc.Get("Cloud_OffloadStateNone");

        OffloadElsewhereText.Visibility = elsewhere.Any ? Visibility.Visible : Visibility.Collapsed;
        if (elsewhere.Any)
            OffloadElsewhereText.Text = Loc.Format("Cloud_OffloadElsewhere", elsewhere.Count);

        // Offered whenever this machine has music in the cloud and nowhere else. A single record or
        // song can be brought back from its own right-click menu. This brings back all of it at once.
        BringBackButton.Visibility = gone.Any && !_cloud.RestoreRunning
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void OnBringBack(object sender, RoutedEventArgs e) => await OfferBringBackAsync();

    /// <summary>
    /// Asks whether the files this machine gave up should come back, and brings them if so.
    ///
    /// <para>Only this machine's own tracks. A cloud-only row is either a track this install deleted
    /// or one it never had. Downloading the second kind would let a laptop copy the desktop's entire
    /// collection. The count, the size and the download are all this install's alone.
    /// </para>
    ///
    /// <para>The free space is checked and stated before anything is asked for. A long download can
    /// fill the disk with no warning at all.</para>
    /// </summary>
    private async Task OfferBringBackAsync()
    {
        if (!_auth.IsLoggedIn || !_library.IsOpen) return;

        var gone = _cloud.OffloadedHere();
        if (!gone.Any) return;

        var free = FreeSpace(_library.Root);

        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock
        {
            Text = Loc.Format("Cloud_BringBackBody", gone.Count,
                ByteSize.Human(gone.Bytes), ByteSize.Human(free)),
            TextWrapping = TextWrapping.Wrap,
        });

        // Stated, not enforced. The sizes are what the files were, and the download is the canonical
        // audio. A user who knows they are about to free space should not be stopped by an estimate.
        // What must not happen is finding out afterwards.
        var enough = free <= 0 || free > gone.Bytes;
        if (!enough)
            body.Children.Add(new TextBlock
            {
                Text = Loc.Format("Cloud_BringBackNoSpace",
                    ByteSize.Human(gone.Bytes), ByteSize.Human(free)),
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x6B, 0x6B)),
            });

        var answer = await PromptDialog.ShowAsync(
            XamlRoot,
            Loc.Get("Cloud_BringBackTitle"),
            body,
            primaryText: Loc.Get("Cloud_BringBackConfirm"),
            closeText: Loc.Get("Cloud_BringBackLeave"),
            defaultButton: enough ? ContentDialogButton.Primary : ContentDialogButton.Close);

        if (answer != ContentDialogResult.Primary) return;

        RenderOffload();
        await _cloud.RestoreOffloadedHereAsync();
        RenderOffload();
    }

    /// <summary>Free space on the drive the library is on. Zero when it cannot be read. Zero means
    /// "unknown", not "no space".</summary>
    private static long FreeSpace(string? root)
    {
        try
        {
            return root is { Length: > 0 } ? new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Clears the failure backoff on the failed tracks and lets the queue try them
    /// again.</summary>
    private void OnRetry(object sender, RoutedEventArgs e)
    {
        if (!_library.IsOpen) return;

        // ClearCloudFailure, not SetCloudState. The state alone left the never-again date and the
        // attempt count on the row. The tracks then left the failed count, but were still never tried.
        var db = _library.Db;
        foreach (var track in db.QueryByCloudState(CloudState.Error, limit: 100_000))
            db.ClearCloudFailure(track.Id);

        // Resume rather than kick. Asking for another try is an instruction to work, so it lifts a
        // pause. A plain kick would be refused by the pause, and the button would do nothing visible.
        _ = _cloud.ResumeAsync();
    }

    private async void OnSignIn(object sender, RoutedEventArgs e)
        => await LoginDialog.ShowAsync(_auth, XamlRoot);

}

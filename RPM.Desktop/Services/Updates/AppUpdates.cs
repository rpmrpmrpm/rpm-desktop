using Microsoft.Extensions.Logging;

using RPM.Desktop.Services.Shell;

using Velopack;

namespace RPM.Desktop.Services.Updates;

/// <summary>
/// The next version of the app: whether there is one, fetching it, and handing over the swap.
/// </summary>
/// <remarks>
/// <para><b>What this does not do.</b> It never replaces a file itself. Velopack's own updater process does
/// that, after this one has gone. The app asks for the package, then asks the updater to wait for this
/// process to exit and to put the new version in place. Everything here is therefore either a network read
/// or a message to another process.</para>
///
/// <para><b>Why the restart is not <c>ApplyUpdatesAndRestart</c>.</b> That call stops this process where it
/// stands. RPM's <c>MainWindow.Closed</c> handler writes the window's place, flushes the session, sends the
/// plays that are waiting, and stops the player and the library. None of that would run. So the updater is
/// told to wait, and then the window is closed in the ordinary way. The app quits as it does on any other
/// day, and comes back as the new version.</para>
///
/// <para><b>A copy that was not installed does nothing at all.</b> A <c>dotnet run</c> build, a bundle
/// copied out of a .dmg by hand, or a machine whose head has no published channel: there is no install for
/// an updater to replace, so the app never asks the network anything. <see cref="Managed"/> is that check.
/// Every other entry point on this class answers harmlessly when it is false.</para>
/// </remarks>
public sealed class AppUpdates
{
    /// <summary>What the app is doing about updates. This is shown on the line in Settings.</summary>
    public enum State
    {
        /// <summary>Nothing has been asked yet.</summary>
        Idle,

        /// <summary>This copy cannot update itself. It is not installed, or its head has no
        /// channel.</summary>
        Unmanaged,

        Checking,

        /// <summary>The feed was read, and this is the newest version.</summary>
        Current,

        Fetching,

        /// <summary>A version is downloaded and waiting for the app to quit.</summary>
        Ready,

        /// <summary>The feed could not be read. This says nothing about whether an update exists.</summary>
        Failed,
    }

    private readonly SettingsService _settings;
    private readonly ILogger<AppUpdates> _log;

    private readonly UpdateManager? _manager;

    /// <summary>The version that is downloaded and waiting, and the asset that the updater is given. It is
    /// held rather than fetched again. The download is on disk, and a second check would find the same
    /// release.</summary>
    private UpdateInfo? _staged;

    /// <summary>Whether an updater has already been started in this run. See <see cref="Apply"/>.</summary>
    private bool _handedOver;

    public AppUpdates(SettingsService settings, ILogger<AppUpdates> log)
    {
        _settings = settings;
        _log = log;

        if (!UpdateChannel.Released)
        {
            _log.LogInformation("[update] this copy was not built by the release. No feed is read.");
            Now = State.Unmanaged;
            return;
        }

        if (UpdateChannel.Current is not { } channel)
        {
            _log.LogInformation("[update] nothing is published for this head. No feed is read.");
            Now = State.Unmanaged;
            return;
        }

        // The channel is stated here, not taken from what the installer wrote into the bundle. Both give
        // the same answer today. Stating it here keeps the rule in one readable place. See UpdateChannel,
        // which also decides the feed that this reads. RpmUpdateManager is Velopack's manager with rpm's
        // rules for delta packages. The feed is the signed one. A feed whose signature does not verify
        // counts as a feed that could not be read.
        _manager = new RpmUpdateManager(
            new SignedWebSource(UpdateChannel.Url(channel), Convert.FromBase64String(FeedSignature.PublicKey), downloader: null),
            new UpdateOptions { ExplicitChannel = channel });

        if (!_manager.IsInstalled)
        {
            _log.LogInformation("[update] this copy was not installed by the installer. No feed is read.");
            _manager = null;
            Now = State.Unmanaged;
        }
    }

    /// <summary>Whether this copy can update itself at all.</summary>
    public bool Managed => _manager is not null;

    /// <summary>What the app is doing about updates. The Settings page reads this.</summary>
    public State Now { get; private set; } = State.Idle;

    /// <summary>The version that is downloaded and waiting, or null.</summary>
    public Version? Staged { get; private set; }

    /// <summary>Raised when <see cref="Now"/> or <see cref="Staged"/> changes. It is raised on whichever
    /// thread made the change. Readers that draw must move themselves onto the dispatcher.</summary>
    public event Action? Changed;

    /// <summary>Whether the question has already been put to the reader in this run. It is asked once per
    /// launch. A check that runs twice, once at startup and once from Settings, must not open two
    /// windows.</summary>
    public bool Asked { get; private set; }

    /// <summary>
    /// Reads the feed, and downloads what it finds. Returns true when a version is staged and waiting.
    /// </summary>
    /// <remarks>
    /// Downloading without asking first is deliberate. The answer to "shall I install it" is worth nothing
    /// if it is followed by a wait. A refused update costs only the download. It is the same package that
    /// the reader gets the next time they are asked.
    /// </remarks>
    public async Task<bool> FetchAsync(CancellationToken ct = default)
    {
        if (_manager is not { } manager)
            return false;

        if (_staged is not null)
            return true;

        try
        {
            Move(State.Checking);

            if (await manager.CheckForUpdatesAsync() is not { } found || ct.IsCancellationRequested)
            {
                _log.LogInformation("[update] {Version} is the newest there is.", manager.CurrentVersion);
                Move(State.Current);
                return false;
            }

            _log.LogInformation(
                "[update] {Version} is out; this copy is {Current}. Fetching it.",
                found.TargetFullRelease.Version, manager.CurrentVersion);

            Move(State.Fetching);
            await manager.DownloadUpdatesAsync(found, cancelToken: ct);

            if (ct.IsCancellationRequested)
            {
                Move(State.Idle);
                return false;
            }

            _staged = found;
            Staged = UpdateAsk.Deferred(found.TargetFullRelease.Version.ToString());
            Move(State.Ready);
            return true;
        }
        catch (OperationCanceledException)
        {
            Move(State.Idle);
            return false;
        }
        catch (Exception ex)
        {
            // A feed that is not there, no network, or a half-written release: none of these is worth a
            // window. The app keeps running as the version it is.
            _log.LogInformation(ex, "[update] the feed could not be read.");
            Move(State.Failed);
            return false;
        }
    }

    /// <summary>Records that the question has been put, so that nothing asks again in this run.</summary>
    public void AskedNow() => Asked = true;

    /// <summary>Whether the reader may be asked about what is staged. This is true when there is something
    /// to ask about, nothing has asked yet in this run, and the version is not the one they last put
    /// off.</summary>
    public bool MayAsk =>
        _staged is not null && !Asked && Staged is { } ready
        && UpdateAsk.Due(ready, UpdateAsk.Deferred(_settings.UpdateDeferred));

    /// <summary>The reader chose Later. This is written down, so that the same version does not ask again
    /// on the next launch. A newer version will still ask.</summary>
    public void Defer()
    {
        if (Staged is { } version)
            _settings.UpdateDeferred = version.ToString();
    }

    /// <summary>
    /// Puts the staged version in place. The updater waits for this process to exit first. So the caller
    /// closes the window afterwards, and lets the app quit the way it always does.
    /// </summary>
    /// <param name="restart">Whether the new version is started once it is in place. True for "Restart
    /// now". False for an update that lands because the reader quit.</param>
    /// <returns>Whether an updater was actually started.</returns>
    /// <remarks>
    /// Only once, whoever asks. "Restart now" hands the update over and then closes the window. Closing the
    /// window also asks for the handover at quit. Without this check, the app would start a second updater,
    /// and the second one would be told NOT to restart. A reader who asked for the new version now would
    /// then get their app replaced and not reopened.
    /// </remarks>
    public bool Apply(bool restart)
    {
        if (_manager is not { } manager || _staged is not { } staged || _handedOver)
            return false;

        try
        {
            manager.WaitExitThenApplyUpdates(staged, silent: true, restart: restart);
            _handedOver = true;
            _log.LogInformation(
                "[update] {Version} will be put in place when this copy exits (restart={Restart}).",
                staged.TargetFullRelease.Version, restart);
            return true;
        }
        catch (Exception ex)
        {
            // The updater could not be started. This happens in a bundle that does not include it, or on a
            // machine that refused to run it. The app is unharmed and stays the version it was.
            _log.LogWarning(ex, "[update] the updater could not be started.");
            return false;
        }
    }

    private void Move(State state)
    {
        if (Now == state)
            return;
        Now = state;
        Changed?.Invoke();
    }
}

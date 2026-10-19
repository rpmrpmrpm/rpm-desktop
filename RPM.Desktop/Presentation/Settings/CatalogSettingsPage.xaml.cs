using RPM.Core.Diagnostics;
using RPM.Desktop.Services;
using RPM.Desktop.Strings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Settings;

/// <summary>Where records get looked up. MusicBrainz needs no account and is always available. Discogs
/// uses the user's own account, which is connected once.
///
/// <para>The user's own account is used, not the app's, for a reason. Discogs counts requests against
/// whoever authorised them. One shared credential would be one request limit for every rpm user to
/// share. Each user connects their own account, so each gets their own limit.</para>
///
/// <para>How the connection ends depends on the platform. On macOS the flow runs in a system sign-in
/// sheet, which hands the callback straight back, so the app knows the result. Elsewhere it happens in
/// the user's browser, and nothing tells the app when the user has finished. So the page checks again
/// when it comes back into view. A Check-again button is offered for when that is not enough.</para>
/// </summary>
public sealed partial class CatalogSettingsPage : Page
{
    private readonly DiscogsService _discogs = App.Services.GetRequiredService<DiscogsService>();
    private readonly FanartService _fanart = App.Services.GetRequiredService<FanartService>();
    private bool _pending;

    /// <summary>Bumped by every fanart.tv status read, save and clear. An answer is drawn only if no
    /// newer one was started after it. This stops a slow status read that finishes after a save from
    /// putting the old state back on the page.</summary>
    private int _fanartAsk;

    public CatalogSettingsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
        // Coming back from the browser is the moment the answer is most likely to have changed.
        GotFocus += async (_, _) => { if (_pending) await RefreshAsync(); };

        // Both rows are about the account, so both follow it while the page is open. Signing in from
        // the Connect button is the usual way this page changes.
        var gate = App.Services.GetRequiredService<Services.Account.AccountGate>();
        gate.Changed += OnAccountChanged;
        Unloaded += (_, _) => gate.Changed -= OnAccountChanged;
    }

    /// <summary>May arrive on any thread: the session is restored on a worker at startup.</summary>
    private void OnAccountChanged() => DispatcherQueue.TryEnqueue(async () => await RefreshAsync());

    private async Task RefreshAsync()
    {
        // No session means there is no question to ask. The connection is held next to the user's rpm
        // account, so without a session it cannot be checked. Asking anyway would only fail, and the
        // failure would be hidden.
        if (!App.Services.GetRequiredService<Services.Account.AccountGate>().IsOpen)
        {
            RenderRpm(signedIn: false);
            RenderSignedOut();
            RenderFanart(null);
            return;
        }

        RenderRpm(signedIn: true);

        var fanart = RefreshFanartAsync();
        var status = await _discogs.RefreshStatusAsync();
        Render(status);
        await fanart;
    }

    /// <summary>
    /// The app's own catalogue. It needs no connection of its own, but it still depends on the account.
    ///
    /// <para>It is reached through the rpm API on this user's session, so when signed out it answers
    /// nothing. The mark follows the account for the same reason as the Discogs mark. A green tick
    /// beside a catalogue that cannot be asked would tell the user something that is not true.</para>
    /// </summary>
    private void RenderRpm(bool signedIn)
    {
        RpmStatusText.Text = Loc.Get(signedIn ? "Catalog_RpmSearched" : "Catalog_RpmSignIn");
        SetMark(RpmIcon, connected: signedIn);
    }

    /// <summary>What the Discogs row says before there is an rpm account behind it. Connect stays
    /// enabled. It is what asks for the sign-in, and connecting carries straight on
    /// afterwards.</summary>
    private void RenderSignedOut()
    {
        _pending = false;
        ErrorText.Visibility = Visibility.Collapsed;
        DiscogsStatusText.Text = Loc.Get("Catalog_DiscogsSignIn");
        SetMark(DiscogsIcon, connected: false);
        ConnectButton.Visibility = Visibility.Visible;
        ConnectButton.IsEnabled = true;
        DisconnectButton.Visibility = Visibility.Collapsed;
        RecheckButton.Visibility = Visibility.Collapsed;
        PendingText.Visibility = Visibility.Collapsed;
    }

    // fanart.tv: a personal key that the user pastes. It is saved to the rpm account.

    private async Task RefreshFanartAsync()
    {
        var ask = ++_fanartAsk;
        var status = await _fanart.StatusAsync();
        if (ask == _fanartAsk)
            RenderFanart(status);
    }

    /// <summary>Draws the fanart.tv row. Null means signed out. The key is saved to the account, so
    /// with no account there is nowhere to save it.</summary>
    private void RenderFanart(FanartKeyStatus? status)
    {
        var signedIn = status is not null;
        var connected = status is { Connected: true };

        FanartStatusText.Text = status switch
        {
            null => Loc.Get("Catalog_FanartSignIn"),
            { Known: false } => Loc.Get("Catalog_FanartStatusUnknown"),
            { Connected: true, Last4: { Length: > 0 } last4 } => Loc.Format("Catalog_FanartConnectedEnding", last4),
            { Connected: true } => Loc.Get("Catalog_FanartConnected"),
            _ => Loc.Get("Catalog_FanartNotConnected"),
        };
        SetMark(FanartIcon, connected);
        FanartKeyBox.IsEnabled = signedIn;
        FanartSaveButton.IsEnabled = signedIn;
        FanartClearButton.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Sends the key to the rpm API. The API checks the key with fanart.tv, and saves it only
    /// if fanart.tv accepts it. So "connected" always means a key that fanart.tv has accepted.
    /// </summary>
    private async void OnSaveFanart(object sender, RoutedEventArgs e)
    {
        var typed = FanartKeyBox.Text?.Trim() ?? "";
        if (typed.Length == 0)
            return;

        var ask = ++_fanartAsk;
        FanartSaveButton.IsEnabled = false;
        ShowFanartNote("Catalog_FanartChecking");
        try
        {
            var (result, status) = await _fanart.SaveAsync(typed);
            if (ask != _fanartAsk)
                return;

            ShowFanartNote(result switch
            {
                FanartKeySave.Saved => null,
                FanartKeySave.Rejected => "Catalog_FanartKeyRejected",
                FanartKeySave.Unchecked => "Catalog_FanartKeyUnchecked",
                _ => "Catalog_FanartSaveFailed",
            });
            if (result == FanartKeySave.Saved)
                FanartKeyBox.Text = "";
            if (status.Known)
                RenderFanart(status);
        }
        finally
        {
            FanartSaveButton.IsEnabled = true;
        }
    }

    private async void OnClearFanart(object sender, RoutedEventArgs e)
    {
        var ask = ++_fanartAsk;
        FanartClearButton.IsEnabled = false;
        try
        {
            var status = await _fanart.ClearAsync();
            if (ask != _fanartAsk)
                return;

            ShowFanartNote(status.Known ? null : "Catalog_FanartSaveFailed");
            if (status.Known)
            {
                FanartKeyBox.Text = "";
                RenderFanart(status);
            }
        }
        finally
        {
            FanartClearButton.IsEnabled = true;
        }
    }

    private void ShowFanartNote(string? key)
    {
        FanartNoteText.Text = key is null ? "" : Loc.Get(key);
        FanartNoteText.Visibility = key is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Render(DiscogsStatus status)
    {
        ErrorText.Visibility = Visibility.Collapsed;

        if (!status.Configured)
        {
            // This is not the user's fault, and they cannot fix it. Say so.
            DiscogsStatusText.Text = Loc.Get("Catalog_DiscogsUnavailable");
            SetMark(DiscogsIcon, connected: false);
            ConnectButton.IsEnabled = false;
            DisconnectButton.Visibility = Visibility.Collapsed;
            RecheckButton.Visibility = Visibility.Collapsed;
            PendingText.Visibility = Visibility.Collapsed;
            return;
        }

        if (status.Connected)
        {
            _pending = false;
            DiscogsStatusText.Text = status.Username is { Length: > 0 } name
                ? Loc.Format("Catalog_DiscogsConnectedAs", name)
                : Loc.Get("Catalog_DiscogsConnected");
            SetMark(DiscogsIcon, connected: true);
            ConnectButton.Visibility = Visibility.Collapsed;
            DisconnectButton.Visibility = Visibility.Visible;
            RecheckButton.Visibility = Visibility.Collapsed;
            PendingText.Visibility = Visibility.Collapsed;
            return;
        }

        DiscogsStatusText.Text = Loc.Get("Catalog_DiscogsNotConnected");
        SetMark(DiscogsIcon, connected: false);
        ConnectButton.Visibility = Visibility.Visible;
        ConnectButton.IsEnabled = true;
        DisconnectButton.Visibility = Visibility.Collapsed;
        RecheckButton.Visibility = _pending ? Visibility.Visible : Visibility.Collapsed;
        PendingText.Visibility = _pending ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        // The whole flow belongs to the accounts service, and it uses this user's bearer token. So
        // nothing can start without a session. Asking for the sign-in is the gate. Connecting carries
        // on as soon as it succeeds.
        if (!await Dialogs.SignInGate.AllowAsync(XamlRoot, Loc.Get("Login_ForDiscogs")))
            return;

        ConnectButton.IsEnabled = false;
        ErrorText.Visibility = Visibility.Collapsed;
        try
        {
            if (MacWebAuth.Applicable)
                await ConnectInSheetAsync();
            else
                await ConnectInBrowserAsync();
        }
        catch (Exception ex)
        {
            ShowError(Loc.Format("Catalog_DiscogsConnectError", Faults.Note("discogs", "connecting Discogs", ex).Say()));
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// macOS: the whole thing happens in a sheet over the app.
    ///
    /// <para>The sheet shares Safari's cookies, so a user who is already signed in to Discogs only
    /// needs to press Approve. The sheet closes itself when the accounts service hands the browser back
    /// to <c>rpm://</c>. App Review requires this flow in a sheet rather than in the default browser
    /// (guideline 4). The sheet returning is also the notification that the user has finished.</para>
    /// </summary>
    private async Task ConnectInSheetAsync()
    {
        var url = await _discogs.StartConnectAsync(MacWebAuth.CallbackScheme);
        var callback = await MacWebAuth.AuthenticateAsync(new Uri(url));

        if (callback is null)
            return;   // The user closed the sheet. Nothing was said, so nothing is shown

        // The connection was made on the server before we were handed back. This only reads what is now
        // true, rather than trusting the redirect.
        await RefreshAsync();

        if (!_discogs.Status.Connected && MacWebAuth.ParseValue(callback, "status") is { } status && status != "connected")
            ShowError(Loc.Get("Catalog_DiscogsConnectFailed"));
    }

    /// <summary>Windows and Linux: the user's own browser. There is no system sign-in sheet here, and
    /// an embedded view is the wrong place for a login page, because the user's password manager and
    /// Discogs session are in their browser. Nothing tells the app when they finish, which is why the
    /// Check-again button exists.
    /// </summary>
    private async Task ConnectInBrowserAsync()
    {
        var url = await _discogs.StartConnectAsync();
        await Windows.System.Launcher.LaunchUriAsync(new Uri(url));

        _pending = true;
        Render(_discogs.Status);
    }

    private async void OnRecheck(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void OnDisconnect(object sender, RoutedEventArgs e)
    {
        try
        {
            await _discogs.DisconnectAsync();
            Render(_discogs.Status);
        }
        catch (Exception ex)
        {
            ShowError(Loc.Format("Catalog_DiscogsDisconnectError", Faults.Note("discogs", "disconnecting Discogs", ex).Say()));
        }
    }

    private void ShowError([Shown] string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    /// <summary>#RRGGBB or #AARRGGBB. Six digits mean opaque, as in CSS, which is what most people
    /// expect when they write a colour by hand. If the top byte of a six-digit value were read as
    /// alpha, every solid colour would be fully transparent.
    /// </summary>
    /// <summary>A catalogue's state, as one mark: the same green tick that the built-in rpm catalogue
    /// shows when it is connected, and a dim dot when it is not. A dot cannot show "working" and "not
    /// working" by itself. Colour alone is not enough, because some people cannot see colour. So the
    /// shape carries the meaning, and the colour only reinforces it.
    /// </summary>
    private static void SetMark(FontIcon icon, bool connected)
    {
        icon.Glyph = connected ? "\uf058" : "\uf111";
        icon.FontSize = connected ? 14 : 12;
        icon.Foreground = Brush(connected ? "#4CD964" : "#66FFFFFF");
    }

    private static Microsoft.UI.Xaml.Media.Brush Brush(string hex)
    {
        var digits = hex.TrimStart('#');
        var value = Convert.ToUInt32(digits, 16);
        if (digits.Length <= 6)
            value |= 0xFF000000;

        return new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(
            (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value));
    }
}

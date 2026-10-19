using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RPM.Core.Sharing;
using RPM.Desktop.Services.Platform;
using RPM.Desktop.Services.Sharing;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Settings;

/// <summary>One computer this one shares with, as the settings list draws it.</summary>
/// <param name="DeviceId">Which machine, for the button beside it.</param>
/// <param name="Name">What it calls itself.</param>
/// <param name="State">Whether it can be reached right now, and whether it is the user's own.</param>
[RpmBindable]
public sealed partial record SharedComputerRow(string DeviceId, string Name, string State);

public sealed partial class LibrarySettingsPage
{
    private SharingService? _sharing;

    /// <summary>Who is signed in. This decides whether any of this card means anything. Sharing is an
    /// arrangement between accounts. See <see cref="SharingService"/>.</summary>
    private RPM.Desktop.Services.Account.AuthService? _shareAuth;

    /// <summary>The list the page draws. It is replaced whole on every change. A short list is not worth
    /// reconciling.</summary>
    private readonly ObservableCollection<SharedComputerRow> _shared = [];

    /// <summary>
    /// Fills the sharing section and keeps it up to date while the page is open.
    ///
    /// <para>Called from the constructor, after the rest of the page has been set up. The switch and
    /// the name box are set inside their own loading guard (<see
    /// cref="LibrarySettingsPage._loading"/>).</para>
    /// </summary>
    private void InitSharing()
    {
        _sharing = _services.GetService<SharingService>();
        _shareAuth = _services.GetService<RPM.Desktop.Services.Account.AuthService>();

        ShareList.ItemsSource = _shared;

        _loading = true;
        ShareToggle.IsOn = _settings.ShareLibrary;
        ShareNameBox.Text = _settings.ShareNameTyped ?? "";
        ShareNameBox.PlaceholderText = WhatTheyWillSee();
        _loading = false;

        RefreshSharing();
        _ = RefreshShareFirewallAsync();

        if (_sharing is { } sharing)
        {
            sharing.Changed += OnSharingChanged;
            Unloaded += (_, _) => sharing.Changed -= OnSharingChanged;
        }

        if (_shareAuth is { } auth)
        {
            auth.Changed += OnShareAccountChanged;
            Unloaded += (_, _) => auth.Changed -= OnShareAccountChanged;
        }
    }

    private void OnSharingChanged() => DispatcherQueue.TryEnqueue(RefreshSharing);

    /// <summary>Somebody signed in or out. May arrive on any thread.</summary>
    private void OnShareAccountChanged() => DispatcherQueue.TryEnqueue(RefreshSharing);

    /// <summary>Whether the card is live. Everything under it (the switch, this computer's name, and
    /// the list of computers it shares with) is an arrangement between accounts. Without an account
    /// they are settings that nothing would act on. The card is dimmed rather than hidden, so the user
    /// can see what signing in would give them.
    /// </summary>
    private void RenderShareAccount()
    {
        var signedIn = _shareAuth?.IsLoggedIn == true;
        ShareSignInPanel.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        SharePanel.Opacity = signedIn ? 1 : 0.4;
        SharePanel.IsHitTestVisible = signedIn;
    }

    private async void OnShareSignIn(object sender, RoutedEventArgs e)
    {
        await Presentation.Dialogs.SignInGate.AllowAsync(XamlRoot, Loc.Get("Login_ForSharing"));
        RefreshSharing();
    }

    private void RefreshSharing()
    {
        RenderShareAccount();

        _shared.Clear();

        var peers = _sharing?.Peers ?? [];
        var seen = _sharing?.Seen ?? [];

        foreach (var peer in peers)
        {
            bool here = seen.Any(p =>
                string.Equals(p.DeviceId, peer.DeviceId, StringComparison.OrdinalIgnoreCase));

            // Two facts, and each is shown only when it says something. A computer of the user's own is
            // worth naming as one. A computer that cannot be reached explains why its music is greyed
            // out.
            var state = peer.Kind is PeerKind.Account
                ? $"{Loc.Get("Settings_ShareMine")}, {Reachability(here)}"
                : Reachability(here);

            _shared.Add(new SharedComputerRow(peer.DeviceId, peer.Name, state));
        }

        var fault = _sharing?.LastFault is { } trouble ? FaultText.Format(trouble.StringKey, trouble.Args) : "";
        ShareFaultText.Text = fault;
        ShareFaultText.Visibility = fault.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        var nearby = _sharing?.Nearby() ?? [];
        ShareNearby.Show(nearby);
        ShareNearby.Visibility = nearby.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        ShareCountText.Text = Loc.Format("Settings_ShareCount", peers.Count);
        ShareEmptyText.Visibility = peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShareCountText.Visibility = peers.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string Reachability(bool here) =>
        Loc.Get(here ? "Settings_ShareOnline" : "Settings_ShareOffline");

    private void OnShareToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        // The service reacts to the setting, rather than being switched directly. So the same thing
        // happens whether the change was made here or somewhere else.
        _settings.ShareLibrary = ShareToggle.IsOn;

        if (ShareToggle.IsOn)
            _ = RefreshShareFirewallAsync();
        else
            RenderShareFirewall();
    }

    /// <summary>The last firewall reading this page asked for and kept. It is Unknown until the first
    /// reading arrives, and it is always Unknown off Windows.</summary>
    private FirewallStanding _shareFirewall = FirewallStanding.Unknown;

    /// <summary>How many readings have been asked for. A reading is used only if it is still the
    /// latest. So a reading started before an Allow cannot land after the reading that followed
    /// it.</summary>
    private int _shareFirewallReads;

    /// <summary>Reads the firewall again and shows the result. It runs on the UI thread: when the page
    /// is built, when sharing is turned on, and after the button is pressed.</summary>
    private async Task RefreshShareFirewallAsync()
    {
        int mine = ++_shareFirewallReads;
        var standing = await WindowsFirewall.ReadAsync();
        if (mine != _shareFirewallReads)
            return;

        _shareFirewall = standing;
        RenderShareFirewall();
    }

    /// <summary>The firewall line. It is shown only while sharing is on. With sharing off, nothing
    /// listens, so the firewall has nothing to block.</summary>
    private void RenderShareFirewall()
    {
        var line = ShareToggle.IsOn ? FirewallText.Line(_shareFirewall, "Firewall_ShareBlocked") : null;
        ShareFirewallText.Text = line ?? "";
        ShareFirewallButton.Content = FirewallText.Button(_shareFirewall);
        ShareFirewallPanel.Visibility = line is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnShareFirewall(object sender, RoutedEventArgs e)
    {
        await WindowsFirewall.FixAsync(_shareFirewall);
        await RefreshShareFirewallAsync();
    }

    private void OnShareNameChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || ShareNameBox.Text.Trim() == (_settings.ShareNameTyped ?? ""))
            return;

        _settings.ShareName = ShareNameBox.Text;

        // Once cleared, the box goes back to showing nothing. The placeholder says what the network
        // will hear instead: the display name, or this machine's name when signed out.
        ShareNameBox.Text = _settings.ShareNameTyped ?? "";
        ShareNameBox.PlaceholderText = WhatTheyWillSee();
    }

    /// <summary>
    /// What the other machines will actually call this one with the box left empty.
    ///
    /// <para>This uses the same rule that the other machines use to read the name, rather than a rule
    /// guessed here. This machine sends a name and a display name, and the far end decides which one
    /// wins. A placeholder that guessed differently would promise something the network does not
    /// do.</para>
    /// </summary>
    private string WhatTheyWillSee() =>
        RPM.Core.Sharing.PeerLabel.For(
            RPM.Core.Sharing.PeerFace.Nobody, _settings.ShareFace, _settings.ShareName);

    private void OnStopSharing(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string deviceId } || _sharing is not { } sharing)
            return;

        sharing.Forget(deviceId);
        RefreshSharing();
    }
}

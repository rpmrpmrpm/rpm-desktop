using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RPM.Desktop.Services.Account;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Setup.Steps;

/// <summary>
/// Step two: the rpm account. Apple and Google sign in (and sign up) in the browser, as the sign-in window
/// does; email and password open that window itself. The step follows the account rather than the button
/// pressed, so a sign-in that finishes any other way still shows as signed in here.
/// </summary>
public sealed partial class WizardAccountStep : UserControl
{
    /// <summary>Next or Skip was pressed.</summary>
    public event EventHandler? Finished;

    private readonly AuthService _auth = App.Services.GetRequiredService<AuthService>();
    private readonly Services.Catalog.DiscogsService _discogs = App.Services.GetRequiredService<Services.Catalog.DiscogsService>();

    private CancellationTokenSource? _waiting;

    /// <summary>True once Discogs has been handed to the browser, until it answers connected. There is
    /// nothing to tell the app when the browser is done, so Check again is offered meanwhile.</summary>
    private bool _discogsPending;

    public WizardAccountStep()
    {
        InitializeComponent();
        Kicker.Text = WizardSteps.Kicker(2);

        _auth.Changed += OnAccountChanged;
        _discogs.StatusChanged += OnAccountChanged;
        Unloaded += (_, _) =>
        {
            _auth.Changed -= OnAccountChanged;
            _discogs.StatusChanged -= OnAccountChanged;
            _waiting?.Cancel();
        };
        Dress();
    }

    /// <summary>May arrive on any thread. A new sign-in also asks where Discogs stands for that account.</summary>
    private void OnAccountChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        Dress();
        if (_auth.IsLoggedIn && !_discogs.Status.Known)
            Arrived();
    });

    /// <summary>Called by the host when this step comes up: asks where Discogs stands, for an account that
    /// was already signed in.</summary>
    public async void Arrived()
    {
        if (!_auth.IsLoggedIn)
            return;
        try { await _discogs.RefreshStatusAsync(); }
        catch { /* the card says not connected, which is what a failed read leaves true */ }
        Dress();
    }

    /// <summary>The cards and the footer, from whether anybody is signed in and where Discogs stands.</summary>
    private void Dress()
    {
        var signedIn = _auth.IsLoggedIn;
        SignInCard.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        SignedInCard.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;

        var discogs = _discogs.Status;
        DiscogsCard.Visibility = signedIn && discogs.Configured ? Visibility.Visible : Visibility.Collapsed;
        if (discogs.Connected)
            _discogsPending = false;
        DiscogsButton.Visibility = discogs.Connected ? Visibility.Collapsed : Visibility.Visible;
        DiscogsRecheck.Visibility = _discogsPending && !discogs.Connected ? Visibility.Visible : Visibility.Collapsed;
        var line = discogs.Connected
            ? Loc.Get("Wizard_DiscogsLinked") + (discogs.Username is { Length: > 0 } user ? $" ({user})" : "")
            : _discogsPending ? Loc.Get("Catalog_DiscogsPending") : null;
        DiscogsStatus.Text = line ?? "";
        DiscogsStatus.Visibility = line is null ? Visibility.Collapsed : Visibility.Visible;
        SignedInText.Text = signedIn
            ? Loc.Format("Wizard_AccountSignedIn", _auth.DisplayName is { Length: > 0 } name ? name : _auth.Email ?? "")
            : "";
        SkipLink.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Apple_Click(object sender, RoutedEventArgs e) => await SocialAsync("Apple");

    private async void Google_Click(object sender, RoutedEventArgs e) => await SocialAsync("Google");

    private async Task SocialAsync(string provider)
    {
        Enable(false);
        Say(Loc.Get("Login_WaitingBrowser"));

        _waiting = new CancellationTokenSource();
        try
        {
            var outcome = await _auth.SignInSocialAsync(provider, _waiting.Token);
            Say(outcome is SignInOutcome.SignedIn ? null : Loc.Get("Login_Unable"));
        }
        catch (OperationCanceledException)
        {
            Say(null);
        }
        catch (Exception ex)
        {
            RPM.Core.Diagnostics.Faults.Note("auth", "setup sign-in", ex);
            Say(Loc.Get("Login_Unable"));
        }
        finally
        {
            _waiting?.Dispose();
            _waiting = null;
            Enable(true);
            Dress();
        }
    }

    private async void SignUp_Click(object sender, RoutedEventArgs e)
    {
        Say(null);
        await Dialogs.SignUpDialog.ShowAsync(_auth, XamlRoot);
        Dress();
    }

    private async void Email_Click(object sender, RoutedEventArgs e)
    {
        Say(null);
        await Dialogs.LoginDialog.ShowAsync(_auth, XamlRoot);
        Dress();
    }

    /// <summary>Links Discogs as the Catalog settings do: a sheet over the app on macOS, the user's own
    /// browser elsewhere (see CatalogSettingsPage.ConnectInSheetAsync and ConnectInBrowserAsync).</summary>
    private async void Discogs_Click(object sender, RoutedEventArgs e)
    {
        DiscogsButton.IsEnabled = false;
        try
        {
            if (MacWebAuth.Applicable)
            {
                var url = await _discogs.StartConnectAsync(MacWebAuth.CallbackScheme);
                if (await MacWebAuth.AuthenticateAsync(new Uri(url)) is not null)
                    await _discogs.RefreshStatusAsync();
            }
            else
            {
                var url = await _discogs.StartConnectAsync();
                await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
                _discogsPending = true;
            }
        }
        catch (Exception ex)
        {
            RPM.Core.Diagnostics.Faults.Note("discogs", "setup connecting Discogs", ex);
            DiscogsStatus.Text = Loc.Get("Catalog_DiscogsConnectFailed");
            DiscogsStatus.Visibility = Visibility.Visible;
            return;
        }
        finally
        {
            DiscogsButton.IsEnabled = true;
        }
        Dress();
    }

    private async void DiscogsRecheck_Click(object sender, RoutedEventArgs e)
    {
        DiscogsRecheck.IsEnabled = false;
        try { await _discogs.RefreshStatusAsync(); }
        catch { }
        finally { DiscogsRecheck.IsEnabled = true; }
        Dress();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        _waiting?.Cancel();
        Finished?.Invoke(this, EventArgs.Empty);
    }

    private void Enable(bool on)
    {
        AppleButton.IsEnabled = on;
        GoogleButton.IsEnabled = on;
        EmailButton.IsEnabled = on;
        SignUpButton.IsEnabled = on;
    }

    private void Say(string? line)
    {
        StatusText.Text = line ?? "";
        StatusText.Visibility = line is null ? Visibility.Collapsed : Visibility.Visible;
    }
}

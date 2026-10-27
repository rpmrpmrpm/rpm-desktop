using RPM.Core.Cloud;
using RPM.Desktop.Presentation.Dialogs;
using RPM.Desktop.Services;
using RPM.Desktop.Strings;
using Microsoft.Extensions.DependencyInjection;

namespace RPM.Desktop.Presentation.Settings;

/// <summary>
/// The account and the facts about it. This is the desktop side of the phone's Profile tab
/// (<c>RPM.Mobile/Presentation/ProfilePage.xaml</c>).
///
/// <para>The supporter status is drawn from <see cref="Services.Account.SupporterService.Current"/>,
/// and only asked for when this session has no answer yet.</para>
/// </summary>
public sealed partial class ProfileSettingsPage : Page
{
    private readonly AuthService _auth;
    private readonly Services.Account.SupporterService _supporter;
    private readonly RpmCommunityClient _community;
    private readonly FolderPickerService _pickers;
    private readonly Services.Shell.SettingsService _settings;

    /// <summary>The profile as the server last stated it. The Save button compares against it, so
    /// pressing Save with nothing changed does not write anything.</summary>
    private RpmProfile? _profile;

    private readonly IServiceProvider _services;

    public ProfileSettingsPage(IServiceProvider services)
    {
        _services = services;
        _auth = services.GetRequiredService<AuthService>();
        _supporter = services.GetRequiredService<Services.Account.SupporterService>();
        _community = services.GetRequiredService<RpmCommunityClient>();
        _pickers = services.GetRequiredService<FolderPickerService>();
        _settings = services.GetRequiredService<Services.Shell.SettingsService>();

        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _auth.Changed += OnAuthChanged;
        _supporter.Changed += OnSupporterChanged;

        ShowAccount();
        ShowPlan();
        ShowDataFolder();
        _ = ShowCacheSizeAsync();
        ShowVersion();
        HookUpdatesCard();
        _ = LoadWallProfileAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _auth.Changed -= OnAuthChanged;
        _supporter.Changed -= OnSupporterChanged;
        UnhookUpdatesCard();
    }

    private void OnAuthChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        ShowAccount();
        ShowPlan();
        _ = LoadWallProfileAsync();
    });

    private void OnSupporterChanged() => DispatcherQueue.TryEnqueue(ShowPlan);


    // Public name and picture, as shown on reviews

    /// <summary>Reads the profile that the review wall uses for this user. If the user has never
    /// written anything, one is created. When signed out there is nothing to read, and the card says
    /// so.
    /// </summary>
    private async Task LoadWallProfileAsync()
    {
        // Signed out the whole account card says so, so there is nothing for this half to add.
        if (!_auth.IsLoggedIn)
        {
            _profile = null;
            return;
        }

        WallPanel.Visibility = Visibility.Visible;

        try
        {
            ShowWallProfile(await _community.GetProfileAsync());
        }
        catch (Exception)
        {
            // The API is not answering. The rest of the page is local and still worth showing.
            WallPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowWallProfile(RpmProfile profile)
    {
        _profile = profile;

        // The network's copy is kept level with this one. Renaming here is when the neighbours' idea of
        // who is at this computer goes stale. Writing it now re-announces the beacon. See
        // PeerFaces.LearnSelf, which is the other way this value arrives.
        _settings.ProfileName = profile.DisplayName;

        DisplayNameBox.Text = profile.DisplayName;
        SaveNameButton.IsEnabled = false;
        WallStatusText.Visibility = Visibility.Collapsed;

        AvatarInitial.Text = profile.DisplayName is { Length: > 0 } n
            ? n[..1].ToUpperInvariant() : "?";

        // THE ADDRESS, NOT A BitmapImage MADE FROM IT. AvatarPicture fetches and decodes the bytes
        // itself. That is the only way it is told how big the picture is. A BitmapImage that fetched
        // its own URL reports a PixelWidth of 0 under Uno. The frame then falls back to the framework's
        // own cover, which anchors to the top left. The result is the picture standing against the left
        // of the circle, with the ground showing down the right. Every other avatar in the app already
        // passes a Uri the same way.
        //
        // The stamp on the URL makes a freshly uploaded picture a different address. Without it, the
        // picture already held for that address would be drawn again.
        AvatarImage.Source = profile.HasAvatar && _community.BaseAddress is { } api
            ? new Uri(api, RpmCommunityClient.AvatarPath(profile.UserId, profile.AvatarUpdatedAt))
            : null;

        RemoveAvatarButton.Visibility = profile.HasAvatar ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnDisplayNameChanged(object sender, TextChangedEventArgs e)
    {
        var typed = DisplayNameBox.Text.Trim();
        SaveNameButton.IsEnabled = typed.Length >= 2 && typed != _profile?.DisplayName;
    }

    private async void OnSaveName(object sender, RoutedEventArgs e)
    {
        var typed = DisplayNameBox.Text.Trim();
        if (typed.Length < 2) return;

        SaveNameButton.IsEnabled = false;

        try
        {
            ShowWallProfile(await _community.SetDisplayNameAsync(typed));
            ShowWallStatus(Loc.Get("Profile_NameSaved"));
        }
        catch (RpmNameTakenException)
        {
            // The one failure here that is the user's to fix, so it is the one that gets its own words.
            ShowWallStatus(Loc.Get("Profile_NameTaken"));
            SaveNameButton.IsEnabled = true;
        }
        catch (Exception)
        {
            ShowWallStatus(Loc.Get("Album_CommunityOffline"));
            SaveNameButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Picks a picture and sends it.
    ///
    /// <para>It is scaled here, not on the server. The wall draws it at 28 pixels, so a camera original
    /// is far bigger than anyone will see. A server function decoding whatever an account holder
    /// uploads would also be a decoder pointed at hostile input. The small picture is what gets
    /// uploaded, and the server returns those same bytes.</para>
    /// </summary>
    private async void OnChooseAvatar(object sender, RoutedEventArgs e)
    {
        if (await _pickers.PickImageFileAsync() is not { Length: > 0 } path) return;

        ChooseAvatarButton.IsEnabled = false;

        try
        {
            var scaled = await Task.Run(() => ArtScaler.Downscale(File.ReadAllBytes(path), AvatarEdge));
            ShowWallProfile(await _community.SetAvatarAsync(scaled));
            ShowWallStatus(Loc.Get("Profile_NameSaved"));
        }
        catch (Exception)
        {
            ShowWallStatus(Loc.Get("Profile_PictureFailed"));
        }
        finally
        {
            ChooseAvatarButton.IsEnabled = true;
        }
    }

    /// <summary>How big an avatar is stored. This is eight times the size a review row draws it. That
    /// covers a high-density screen, and leaves room for the settings card's own preview.</summary>
    private const int AvatarEdge = 256;

    private async void OnRemoveAvatar(object sender, RoutedEventArgs e)
    {
        try
        {
            ShowWallProfile(await _community.ClearAvatarAsync());
        }
        catch (Exception)
        {
            ShowWallStatus(Loc.Get("Album_CommunityOffline"));
        }
    }

    private void ShowWallStatus([Shown] string text)
    {
        WallStatusText.Text = text;
        WallStatusText.Visibility = Visibility.Visible;
    }

    // Account

    private void ShowAccount()
    {
        var signedIn = _auth.IsLoggedIn;
        SignedInPanel.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        SignedOutPanel.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;

        NameText.Text = _auth.DisplayName is { Length: > 0 } name ? name : _auth.Email ?? "";
        EmailText.Text = _auth.Email ?? "";

        // With no name to show, the address is the heading rather than a second empty line under one.
        EmailText.Visibility = NameText.Text == EmailText.Text
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    /// <summary>
    /// Signs out, and stops there.
    ///
    /// <para>The phone clears everything it holds, because a phone's library is the cloud's. This
    /// machine's music is the user's own files in their own folder. Signing out ends the session and
    /// stops the sync. It does not touch a single track.</para>
    /// </summary>
    private void OnSignOut(object sender, RoutedEventArgs e) => _auth.Logout();

    /// <summary>Deletes the account, after the person types its address. The dialog stays open while
    /// the server answers. A refusal is shown in it, and the person can try again. On success the app
    /// is signed out and says what happened. Everything after the button is <see
    /// cref="Services.Account.AccountDeletion"/>.
    /// </summary>
    private async void OnDeleteAccount(object sender, RoutedEventArgs e)
    {
        if (_auth.Email is not { Length: > 0 } email || XamlRoot is not { } root)
            return;

        var deletion = _services.GetRequiredService<Services.Account.AccountDeletion>();

        var body = Loc.Get("Account_DeleteBody") + "\n\n" + Loc.Get("Account_DeleteKeepsFiles");
        if (CloudOnlyCount() is var only and > 0)
            body += " " + (only == 1 ? Loc.Get("Account_DeleteCloudOnlyOne") : Loc.Format("Account_DeleteCloudOnly", only));

        var deleted = await PromptDialog.ConfirmByTypingAsync(
            root,
            title: Loc.Get("Account_DeleteTitle"),
            body: body,
            typePrompt: Loc.Format("Account_DeleteTypeEmail", email),
            mustType: email,
            actionText: Loc.Get("Account_Delete"),
            workingText: Loc.Get("Account_Deleting"),
            failedText: Loc.Get("Account_DeleteFailed"),
            cancelText: Loc.Get("Common_Cancel"),
            act: async ct => await deletion.DeleteAsync(email, ct) switch
            {
                Services.Account.AccountDeletionOutcome.Deleted => null,
                Services.Account.AccountDeletionOutcome.EmailRefused => Loc.Get("Account_DeleteEmailRefused"),
                _ => Loc.Get("Account_DeleteFailed"),
            });

        if (deleted && XamlRoot is { } still)
            await PromptDialog.ShowAsync(still, Loc.Get("Account_DeletedTitle"), Loc.Get("Account_DeletedBody"),
                closeText: Loc.Get("Common_Ok"));
    }

    /// <summary>The tracks on this computer whose only copy is in the cloud: offloaded and not brought
    /// back. Deleting the account loses them, so the confirmation says how many.</summary>
    private int CloudOnlyCount()
    {
        var library = _services.GetRequiredService<Services.Library.LibraryService>();
        return library.IsOpen ? library.Db.OffloadedHereTally().Count : 0;
    }

    private async void OnSignIn(object sender, RoutedEventArgs e)
        => await LoginDialog.ShowAsync(_auth, XamlRoot);

    // The plan
    //
    // Which of the two plans this account is on. The storage meter is on the Cloud page, next to the
    // sync engine that fills it. What the plan allows is read here. What has been put in it is read
    // there.

    private void ShowPlan()
    {
        // Signed out there is no account to show a status for.
        PlanCard.Visibility = _auth.IsLoggedIn ? Visibility.Visible : Visibility.Collapsed;
        if (!_auth.IsLoggedIn)
            return;

        var status = _supporter.Current;

#if DEBUG
        // The debug switch on the cloud page also changes this card.
        if (_supporter.Pretend is not null)
            status = _supporter.Standing;
#endif

        // No answer yet this session: ask, and draw when Changed fires. The card stays hidden until
        // then, rather than showing a status that was never read.
        if (status is null)
        {
            PlanCard.Visibility = Visibility.Collapsed;
            _ = _supporter.GetAsync();
            return;
        }

        // Three standings: free, closed beta tester (the supporter features, no badge) and supporter.
        var paying = status is { IsSupporter: true, IsTester: false };
        FreeStanding.Visibility = !status.IsSupporter ? Visibility.Visible : Visibility.Collapsed;
        TesterStanding.Visibility = status.IsTester ? Visibility.Visible : Visibility.Collapsed;
        SupporterStanding.Visibility = paying ? Visibility.Visible : Visibility.Collapsed;
        PlanBadge.IsSupporter = paying;
    }

    // Local data and cache

    private void ShowDataFolder() => DataPathText.Text = LocalData.Dir;

    /// <summary>The size is counted from the disk, which means a folder walk. It runs off the UI
    /// thread, and it may be a moment late.</summary>
    private async Task ShowCacheSizeAsync()
    {
        var bytes = await Task.Run(AppCache.BytesOnDisk);
        CacheSizeText.Text = Loc.Format("Profile_CacheUsed", ByteSize.Human(bytes));
    }

    private async void OnClearCache(object sender, RoutedEventArgs e)
    {
        ClearCacheButton.IsEnabled = false;
        CacheStatusText.Text = "";
        try
        {
            await Task.Run(AppCache.Clear);
            CacheStatusText.Text = Loc.Get("Profile_CacheCleared");
            await ShowCacheSizeAsync();
        }
        finally
        {
            ClearCacheButton.IsEnabled = true;
        }
    }

    private void OnShowFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(LocalData.Dir);
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(LocalData.Dir) { UseShellExecute = true });
        }
        catch
        {
            // No file manager, or a sandbox that will not open one. The path is on screen either way,
            // which is the part that matters.
        }
    }

    // About

    private void ShowVersion() => VersionText.Text = Loc.Format("Rpm_Profile_Version", App.VersionText);
}

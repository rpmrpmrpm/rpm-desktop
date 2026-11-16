using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using RPM.Core.Cloud;
using SkiaSharp;
using Windows.UI;

namespace RPM.Desktop.Presentation.Main.LeftPane;

/// <summary>
/// The reader's edit page, as on the phones: one column of cards (display name, profile photo, banner, badge
/// and its colour) with Save at the foot. Nothing is sent until Save. Leaving with changes asks "Apply
/// changes" or "Discard" (<see cref="MayLeaveAsync"/>).
///
/// <para>Pictures are cut to their shape and size here, as the phones do (<c>PicturePrep</c>): the photo to a
/// 512 px square under 500 KB, the banner to 1500 by 500 under 1.9 MB, both JPEG. The server keeps what it
/// is given.</para>
/// </summary>
internal sealed class FeedEdit
{
    /// <summary>The badge colours, the phones' list (<c>ProfileBadges.colours</c>); the first is the default.</summary>
    private static readonly string[] Colours =
        ["#7A1FD1", "#2563EB", "#0E9F95", "#2E9E4F", "#D12A4E", "#E0701B", "#D63FA6", "#B8901F"];

    private static RpmCommunityClient Community => App.Services.GetRequiredService<RpmCommunityClient>();

    private readonly RpmPersonProfile _person;
    private readonly StackPanel _column = new() { Spacing = 12, Padding = new Thickness(16, 7, 16, 40) };
    private readonly TextBox _name;
    private readonly AvatarPicture _photo = new() { CornerRadius = new CornerRadius(36) };
    private readonly Grid _photoPlate;
    private readonly AvatarPicture _banner = new() { CornerRadius = new CornerRadius(12) };
    private readonly RadioButton _bannerOwn;
    private readonly RadioButton _bannerPlaying;
    private readonly StackPanel _bannerKeys;
    private readonly ChipWrap _badges = new() { Spacing = 8 };
    private readonly StackPanel _colours = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly Button _save;
    private readonly TextBlock _trouble;

    // What Save sends. Null pictures mean "unchanged"; the Remove flags mean "take it away".
    private byte[]? _newPhoto;
    private bool _removePhoto;
    private byte[]? _newBanner;
    private bool _removeBanner;
    private string _badge;
    private string _colour;
    private bool _saving;

    public FeedEdit(RpmPersonProfile person)
    {
        _person = person;
        _badge = person.Badge ?? "";
        _colour = person.BadgeColour is { Length: 7 } c ? c : "";

        _column.Children.Add(Title(Loc.Get("Rpm_Profile_EditPage")));

        // Display name.
        _name = new TextBox { Text = person.DisplayName, MaxLength = 40, FontSize = 14 };
        _name.TextChanged += (_, _) => Sync();
        _column.Children.Add(Card(Loc.Get("Rpm_Profile_Name"), _name));

        // Profile photo.
        _photoPlate = new Grid { Width = 72, Height = 72, CornerRadius = new CornerRadius(36), Background = FeedCards.White(0x26) };
        _photoPlate.Children.Add(_photo);
        var photoKeys = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        photoKeys.Children.Add(Key(Loc.Get("Rpm_Profile_ChangePhoto"), async () => await PickAsync(banner: false)));
        photoKeys.Children.Add(Key(Loc.Get("Rpm_Profile_RemovePhoto"), () =>
        {
            _newPhoto = null;
            _removePhoto = true;
            ShowPhoto();
            Sync();
        }));
        var photoRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        photoRow.Children.Add(_photoPlate);
        photoRow.Children.Add(photoKeys);
        _column.Children.Add(Card(Loc.Get("Rpm_Profile_PhotoTitle"), photoRow));

        // Banner: the uploaded picture, or the photo of the artist playing.
        var bannerPlate = new Grid { Height = 110, CornerRadius = new CornerRadius(12), Background = FeedCards.White(0x14) };
        bannerPlate.Children.Add(_banner);
        _bannerOwn = new RadioButton { Content = Loc.Get("Rpm_Profile_BannerOwn"), GroupName = "banner", IsChecked = !person.BannerFollowsListening };
        _bannerPlaying = new RadioButton { Content = Loc.Get("Rpm_Profile_BannerPlaying"), GroupName = "banner", IsChecked = person.BannerFollowsListening };
        _bannerOwn.Checked += (_, _) => { ShowBanner(); Sync(); };
        _bannerPlaying.Checked += (_, _) => { ShowBanner(); Sync(); };
        _bannerKeys = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _bannerKeys.Children.Add(Key(Loc.Get("Rpm_Profile_ChooseBanner"), async () => await PickAsync(banner: true)));
        _bannerKeys.Children.Add(Key(Loc.Get("Rpm_Profile_RemoveBanner"), () =>
        {
            _newBanner = null;
            _removeBanner = true;
            ShowBanner();
            Sync();
        }));
        var bannerBody = new StackPanel { Spacing = 8 };
        bannerBody.Children.Add(bannerPlate);
        bannerBody.Children.Add(_bannerOwn);
        bannerBody.Children.Add(_bannerPlaying);
        bannerBody.Children.Add(_bannerKeys);
        _column.Children.Add(Card(Loc.Get("Rpm_Profile_BannerTitle"), bannerBody));

        // Badge and its colour. Only badges already unlocked can be shown; the phones unlock them.
        var badgeBody = new StackPanel { Spacing = 10 };
        badgeBody.Children.Add(_badges);
        badgeBody.Children.Add(new TextBlock { Text = Loc.Get("Rpm_Badges_Colour"), FontSize = 12, Foreground = FeedCards.White(0x99) });
        badgeBody.Children.Add(_colours);
        _column.Children.Add(Card(Loc.Get("Rpm_Badges_Title"), badgeBody));

        _save = new RpmPrimaryButton { Content = Loc.Get("Rpm_Profile_Save"), HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = false };
        _save.Click += async (_, _) => await SaveAsync();
        _column.Children.Add(_save);
        _trouble = new TextBlock { FontSize = 12, Foreground = FeedCards.White(0xAA), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        _column.Children.Add(_trouble);

        Root = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _column,
        };
        ShowPhoto();
        ShowBanner();
        ShowBadges();
    }

    public ScrollViewer Root { get; }

    /// <summary>Save went through. The page closes and the profile is read again.</summary>
    public event Action? Saved;

    private bool BannerFollows => _bannerPlaying.IsChecked == true;

    private bool Changed =>
        _name.Text.Trim() != _person.DisplayName
        || _newPhoto is not null || _removePhoto
        || _newBanner is not null || _removeBanner
        || BannerFollows != _person.BannerFollowsListening
        || _badge != (_person.Badge ?? "")
        || _colour != (_person.BadgeColour ?? "");

    private void Sync() => _save.IsEnabled = !_saving && Changed && _name.Text.Trim().Length >= 2;

    /// <summary>Asked before the page closes. Without changes it closes. With changes the reader chooses:
    /// apply (save, and close only when saved), discard, or stay.</summary>
    public async Task<bool> MayLeaveAsync()
    {
        if (!Changed || _saving)
            return !_saving;
        var answer = await PromptDialog.ShowAsync(
            Root.XamlRoot,
            Loc.Get("Rpm_Profile_UnsavedTitle"),
            null,
            primaryText: Loc.Get("Rpm_Profile_ApplyChanges"),
            secondaryText: Loc.Get("Rpm_Profile_Discard"),
            closeText: Loc.Get("Common_Cancel"),
            defaultButton: ContentDialogButton.Primary);
        return answer switch
        {
            ContentDialogResult.Primary => await SaveAsync(),
            ContentDialogResult.Secondary => true,
            _ => false,
        };
    }

    // ── what the cards show ──

    private void ShowPhoto()
    {
        var api = Community.BaseAddress;
        _photo.Source = _newPhoto is { } bytes ? Bitmap(bytes)
            : !_removePhoto && _person.HasAvatar && api is not null
                ? new Uri(api, RpmCommunityClient.AvatarPath(_person.UserId, _person.AvatarUpdatedAt))
                : null;
    }

    private void ShowBanner()
    {
        _bannerKeys.Visibility = BannerFollows ? Visibility.Collapsed : Visibility.Visible;
        var api = Community.BaseAddress;
        if (BannerFollows)
        {
            _banner.Source = null;
            return;
        }
        _banner.Source = _newBanner is { } bytes ? Bitmap(bytes)
            : !_removeBanner && _person.HasBanner && api is not null
                ? new Uri(api, RpmCommunityClient.BannerPath(_person.UserId, _person.BannerUpdatedAt))
                : null;
    }

    private static Microsoft.UI.Xaml.Media.Imaging.BitmapImage Bitmap(byte[] bytes)
    {
        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
        _ = bitmap.SetSourceAsync(BitmapBytes.Stream(bytes));
        return bitmap;
    }

    private void ShowBadges()
    {
        _badges.Children.Clear();
        _badges.Children.Add(Choice(Loc.Get("Rpm_Badges_NoBadge"), _badge.Length == 0, () => _badge = ""));
        foreach (var id in _person.Badges ?? [])
        {
            if (ProfileBadges.NameOf(id) is { } name)
                _badges.Children.Add(Choice(name, _badge == id, () => _badge = id));
        }

        _colours.Children.Clear();
        foreach (var hex in Colours)
        {
            var colour = ProfileBadges.ColourOf(hex);
            var chosen = string.Equals(_colour.Length == 0 ? Colours[0] : _colour, hex, StringComparison.OrdinalIgnoreCase);
            var swatch = new Grid { Width = 26, Height = 26 };
            swatch.Children.Add(new Ellipse { Fill = new SolidColorBrush(colour) });
            if (chosen)
                swatch.Children.Add(new Ellipse { Stroke = FeedCards.White(0xFF), StrokeThickness = 2, Margin = new Thickness(-4) });
            var key = new Button { Style = FeedCards.Key(), Height = double.NaN, Content = swatch, Padding = new Thickness(4) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(key, hex);
            var pick = hex;
            key.Click += (_, _) =>
            {
                _colour = pick == Colours[0] ? "" : pick;
                ShowBadges();
                Sync();
            };
            _colours.Children.Add(key);
        }
    }

    private Button Choice(string text, bool chosen, Action choose)
    {
        var key = new Button
        {
            Style = FeedCards.Key(),
            Height = 30,
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(15),
            BorderThickness = new Thickness(1),
            BorderBrush = FeedCards.White(chosen ? (byte)0xEE : (byte)0x33),
            Background = FeedCards.White(chosen ? (byte)0x26 : (byte)0x0C),
            Content = new TextBlock { Text = text, FontSize = 13, FontStyle = Windows.UI.Text.FontStyle.Italic, Foreground = FeedCards.White(0xEE) },
        };
        key.Click += (_, _) =>
        {
            choose();
            ShowBadges();
            Sync();
        };
        return key;
    }

    // ── pictures ──

    private async Task PickAsync(bool banner)
    {
        var pickers = App.Services.GetRequiredService<FolderPickerService>();
        if (await pickers.PickImageFileAsync() is not { Length: > 0 } path)
            return;
        var prepared = await Task.Run(() =>
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                return banner ? ProfilePictures.Jpeg(bytes, 1500, 500, 1_900_000) : ProfilePictures.Jpeg(bytes, 512, 512, 500_000);
            }
            catch (IOException)
            {
                return null;
            }
        });
        if (prepared is null)
        {
            Say(Loc.Get("Rpm_Profile_PictureFailed"));
            return;
        }
        if (banner)
        {
            _newBanner = prepared;
            _removeBanner = false;
            ShowBanner();
        }
        else
        {
            _newPhoto = prepared;
            _removePhoto = false;
            ShowPhoto();
        }
        Say(null);
        Sync();
    }

    // ── saving ──

    /// <summary>Sends what changed, one call at a time. Answers whether all of it was saved.</summary>
    private async Task<bool> SaveAsync()
    {
        if (_saving)
            return false;
        _saving = true;
        Sync();
        Say(null);
        try
        {
            var name = _name.Text.Trim();
            if (name != _person.DisplayName)
                await Community.SetDisplayNameAsync(name);
            if (_newPhoto is { } photo)
                await Community.SetAvatarAsync(photo);
            else if (_removePhoto && _person.HasAvatar)
                await Community.ClearAvatarAsync();
            if (_newBanner is { } banner)
                await Community.SetBannerAsync(banner);
            else if (_removeBanner && _person.HasBanner)
                await Community.ClearBannerAsync();
            if (BannerFollows != _person.BannerFollowsListening)
                await Community.SetBannerSourceAsync(BannerFollows);
            if (_badge != (_person.Badge ?? "") || _colour != (_person.BadgeColour ?? ""))
                await Community.SetBadgeAsync(_badge, _colour);
        }
        catch (RpmNameTakenException)
        {
            return Failed(Loc.Get("Profile_NameTaken"));
        }
        catch (RpmCommunityRejectedException)
        {
            return Failed(Loc.Get("Rpm_Profile_PictureFailed"));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return Failed(Loc.Get("Rpm_Profile_SaveFailed"));
        }
        _saving = false;
        Saved?.Invoke();
        return true;
    }

    private bool Failed(string said)
    {
        _saving = false;
        Say(said);
        Sync();
        return false;
    }

    private void Say(string? text)
    {
        _trouble.Text = text ?? "";
        _trouble.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ── parts ──

    private static TextBlock Title(string text) => new()
    {
        Text = text,
        FontSize = 17,
        FontWeight = FontWeights.SemiBold,
        Foreground = FeedCards.White(0xFF),
        Margin = new Thickness(0, 0, 0, 4),
    };

    private static Border Card(string title, UIElement body)
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = FeedCards.White(0xCC) });
        stack.Children.Add(body);
        return new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = FeedCards.White(0x12),
            Padding = new Thickness(14),
            Child = stack,
        };
    }

    private static Button Key(string text, Action onClick)
    {
        var key = new RpmButton { Content = text };
        key.Click += (_, _) => onClick();
        return key;
    }

}

/// <summary>Makes a picked picture small enough for the server: JPEG, cut to the shape it is shown in, as
/// the phones' <c>PicturePrep</c>.</summary>
internal static class ProfilePictures
{
    /// <summary>Draws the picture to fill <paramref name="wide"/> by <paramref name="high"/> (never larger than
    /// the picture itself), cut at the centre, and lowers the JPEG quality until it is under
    /// <paramref name="limit"/> bytes. Null when it does not decode or does not fit.</summary>
    public static byte[]? Jpeg(byte[] bytes, int wide, int high, int limit)
    {
        using var source = SKBitmap.Decode(bytes);
        if (source is null || source.Width <= 0 || source.Height <= 0)
            return null;

        var fill = Math.Max((double)wide / source.Width, (double)high / source.Height);
        var outWide = fill > 1 ? Math.Max(1, (int)(wide / fill)) : wide;
        var outHigh = fill > 1 ? Math.Max(1, (int)(high / fill)) : high;
        var scale = Math.Max((double)outWide / source.Width, (double)outHigh / source.Height);
        var cropWide = (float)(outWide / scale);
        var cropHigh = (float)(outHigh / scale);
        var left = (source.Width - cropWide) / 2;
        var top = (source.Height - cropHigh) / 2;

        using var surface = SKSurface.Create(new SKImageInfo(outWide, outHigh));
        using (var image = SKImage.FromBitmap(source))
        {
            // Cubic sampling: SkiaSharp 3 draws with nearest-neighbour when no sampling is given.
            surface.Canvas.DrawImage(image, SKRect.Create(left, top, cropWide, cropHigh),
                SKRect.Create(0, 0, outWide, outHigh), new SKSamplingOptions(SKCubicResampler.Mitchell));
        }
        using var snapshot = surface.Snapshot();
        foreach (var quality in new[] { 85, 70, 55, 40 })
        {
            using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, quality);
            if (data is not null && data.Size <= limit)
                return data.ToArray();
        }
        return null;
    }
}

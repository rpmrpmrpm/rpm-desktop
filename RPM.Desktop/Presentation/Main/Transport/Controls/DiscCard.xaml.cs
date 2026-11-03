using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using RPM.Core.Ripping;
using RPM.Desktop.Presentation.Dialogs;
using RPM.Desktop.Services.Library;
using RPM.Desktop.Strings;
using RPM.Fingerprint.Catalog;

namespace RPM.Desktop.Presentation.Main.Transport.Controls;

/// <summary>
/// The card that comes up when an audio CD goes into the drive (Chivan, 2026-11-03): the record turning
/// while the disc is read and looked up, then its sleeve, its name and its artist, and the choice of playing
/// it as it is or ripping it into the collection.
///
/// <para>It is the face of <see cref="RipJobService"/>'s offer. The offer opens it, and anything that ends the
/// offer (closing it here, choosing either button, a rip started from Add music) puts it away. It rises from
/// the bottom of the window and sinks back into it, fading as it goes.</para>
/// </summary>
public sealed partial class DiscCard : Microsoft.UI.Xaml.Controls.UserControl
{
    private static readonly TimeSpan In = TimeSpan.FromMilliseconds(320);
    private static readonly TimeSpan Out = TimeSpan.FromMilliseconds(220);
    private const double Travel = 36;

    private readonly RipJobService _rips;
    private readonly RipperService _ripper;
    private CancellationTokenSource? _lookup;
    private Storyboard? _motion;
    private bool _up;

    private DiscInfo? _disc;
    private CatalogDisc? _listing;
    private byte[]? _cover;

    public DiscCard()
    {
        InitializeComponent();
        _rips = App.Services.GetRequiredService<RipJobService>();
        _ripper = App.Services.GetRequiredService<RipperService>();

        Loaded += (_, _) =>
        {
            _rips.Changed += OnOffer;
            OnOffer();
        };
        Unloaded += (_, _) => _rips.Changed -= OnOffer;
    }

    private void OnOffer() => DispatcherQueue.TryEnqueue(() =>
    {
        var offered = _rips.State == RipJobState.Offered;
        if (offered && !_up)
            Show();
        else if (!offered && _up)
            Hide();
    });

    private void Show()
    {
        _up = true;
        _disc = null;
        _listing = null;
        _cover = null;

        Spinner.Spinning = true;
        Spinner.Visibility = Visibility.Visible;
        NoCover.Visibility = Visibility.Collapsed;
        Cover.Source = null;
        Cover.Opacity = 0;
        AlbumText.Text = Loc.Get("Rip_CardLooking");
        ArtistText.Text = "";
        PlayButton.IsEnabled = false;

        Visibility = Visibility.Visible;
        Move(from: Travel, to: 0, fromOpacity: 0, toOpacity: 1, In, done: null);

        _lookup?.Cancel();
        _lookup = new CancellationTokenSource();
        _ = LookAsync(_lookup.Token);
    }

    private void Hide()
    {
        _up = false;
        _lookup?.Cancel();
        Move(from: 0, to: Travel, fromOpacity: Card.Opacity, toOpacity: 0, Out, done: () =>
        {
            if (_up)
                return;   // offered again while it sank
            Visibility = Visibility.Collapsed;
            Spinner.Spinning = false;
        });
    }

    /// <summary>Reads the disc in the drive, then asks the catalogue what it is, then fetches its sleeve. Each
    /// answer is painted as it comes; a step that cannot answer leaves the card saying what it knows.</summary>
    private async Task LookAsync(CancellationToken ct)
    {
        try
        {
            foreach (var drive in await _ripper.ListDrivesAsync(ct))
            {
                if (await _ripper.ReadDiscAsync(drive, ct) is { } disc && disc.Toc.AudioTracks.Any())
                {
                    _disc = disc;
                    break;
                }
            }
            if (ct.IsCancellationRequested)
                return;

            // A disc in hand can be played, known or not.
            PlayButton.IsEnabled = _disc is not null;
            if (_disc is null)
            {
                Unknown();
                return;
            }

            if ((await _ripper.LookupDiscAsync(_disc, ct)).Answers(out var listing, out _) && listing is not null)
                _listing = listing;
            if (ct.IsCancellationRequested)
                return;
            if (_listing is null)
            {
                Unknown();
                return;
            }

            AlbumText.Text = _listing.Title;
            ArtistText.Text = _listing.Artist ?? "";

            var art = await CdPlayService.SleeveAsync(_listing, ct);
            if (ct.IsCancellationRequested)
                return;
            if (art is { Length: > 0 })
            {
                _cover = art;
                await ShowCoverAsync(art);
            }
            else
            {
                NoCover.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            if (!ct.IsCancellationRequested)
                Unknown();
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                Spinner.Spinning = false;
                Spinner.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void Unknown()
    {
        AlbumText.Text = Loc.Get("Rip_OfferTitle");
        ArtistText.Text = Loc.Get("Rip_CardUnknown");
        NoCover.Visibility = Visibility.Visible;
    }

    private async Task ShowCoverAsync(byte[] bytes)
    {
        var image = new BitmapImage { DecodePixelWidth = 264 };
        using var stream = new MemoryStream(bytes).AsRandomAccessStream();
        await image.SetSourceAsync(stream);
        Cover.Source = image;

        var fade = new Storyboard();
        var animation = new DoubleAnimation { From = 0, To = 1, Duration = new Duration(In) };
        Storyboard.SetTarget(animation, Cover);
        Storyboard.SetTargetProperty(animation, "Opacity");
        fade.Children.Add(animation);
        fade.Begin();
    }

    /// <summary>Slides the card on its way up or down and fades it with the slide.</summary>
    private void Move(double from, double to, double fromOpacity, double toOpacity, TimeSpan time, Action? done)
    {
        _motion?.Stop();
        Rise.Y = from;
        Card.Opacity = fromOpacity;

        var motion = new Storyboard();
        var ease = new CubicEase { EasingMode = to < from ? EasingMode.EaseOut : EasingMode.EaseIn };
        var slide = new DoubleAnimation { From = from, To = to, Duration = new Duration(time), EasingFunction = ease };
        Storyboard.SetTarget(slide, Rise);
        Storyboard.SetTargetProperty(slide, "Y");
        var fade = new DoubleAnimation { From = fromOpacity, To = toOpacity, Duration = new Duration(time), EasingFunction = ease };
        Storyboard.SetTarget(fade, Card);
        Storyboard.SetTargetProperty(fade, "Opacity");
        motion.Children.Add(slide);
        motion.Children.Add(fade);
        motion.Completed += (_, _) =>
        {
            // Stopping a storyboard puts its properties back to where they were before it, so the end is
            // written by hand: a card that sank stays down, and one that rose stays up.
            Rise.Y = to;
            Card.Opacity = toOpacity;
            done?.Invoke();
        };
        _motion = motion;
        motion.Begin();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => _rips.CloseOffer();

    /// <summary>Plays the disc as it is: the first track starts once it is read, the rest follow it into the
    /// queue (see <see cref="CdPlayService"/>).</summary>
    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_disc is not { } disc)
            return;
        var (listing, cover) = (_listing, _cover);
        _rips.CloseOffer();
        _ = App.Services.GetRequiredService<CdPlayService>().PlayAsync(disc, listing, cover);
    }

    /// <summary>Opens Import from CD on this disc, as Add music does.</summary>
    private async void Rip_Click(object sender, RoutedEventArgs e)
    {
        _rips.CloseOffer();
        await RipDiscDialog.ShowForAsync(XamlRoot, DiscKind.Cd);
    }
}

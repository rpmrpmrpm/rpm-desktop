using RPM.Core.Ripping;
using RPM.Desktop.Presentation.Dialogs;
using RPM.Desktop.Services;
using RPM.Desktop.Strings;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace RPM.Desktop.Presentation.Main.Transport.Controls;

/// <summary>
/// The deck's rip chip: a disc with a progress ring. It is shown only while a rip is running, or just
/// after it has failed. It is a minimised Add-music popup. The flyout has the same status line, bar and
/// list of landed tracks. Everything it shows comes from <see cref="RipJobService"/>, so the chip and
/// the popup are two views of one job.
///
/// <para>It is also where a disc announces itself. When a CD is put in the drive, the chip appears with
/// its flyout already open, asking whether to rip it. This is the same chip, one step earlier in the same
/// process, not a separate notification. Saying yes opens the disc window. Saying no removes the chip
/// from the deck until the next disc.</para>
/// </summary>
public sealed partial class RipChip : UserControl
{
    // The ring's colours come from the rpm palette. Cyan while the disc turns. Gold and the rpm red are
    // used for the two endings that need a colour. Gold is the hue the review grid's dots and the
    // waveform's silence marks already use, because a rip that came back one track short did not fail.
    private static readonly Color Cyan = Color.FromArgb(0xFF, 0x04, 0xC8, 0xFF);
    private static readonly Color Red = Color.FromArgb(0xFF, 0xFF, 0x6B, 0x6B);
    private static readonly Color Gold = Color.FromArgb(0xFF, 0xEF, 0xAF, 0x12);

    /// <summary>Ring geometry, in the 24×24 button box.</summary>
    private const double Centre = 12;
    private const double Radius = 9;
    private const int RingSegments = 48;

    /// <summary>A rip that has just started still shows a small arc, so the ring never looks empty while it
    /// runs.</summary>
    private const double MinArc = 0.04;

    private readonly RipJobService _rips;

    /// <summary>Whether the question on screen has already opened for the user. The offer opens its own
    /// flyout. That is its purpose: it asks, rather than showing a badge. It must open once per disc, not
    /// again on every progress update.</summary>
    private bool _offerShown;

    public RipChip()
    {
        InitializeComponent();

        _rips = App.Services.GetRequiredService<RipJobService>();
        TrackList.ItemsSource = _rips.Tracks;

        FillRing(RingTrack, 1);

        Loaded += (_, _) =>
        {
            _rips.Changed += OnJobChanged;
            Sync();
        };
        Unloaded += (_, _) => _rips.Changed -= OnJobChanged;
    }

    private void OnJobChanged() => Sync();

    private void Sync()
    {
        bool show = _rips.State != RipJobState.Idle;
        Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            // Nothing to report. An open flyout here would be an empty panel over a chip that is gone.
            StatusFlyout.Hide();
            return;
        }

        // A disc is waiting for an answer. The chip is shown, but there is no job yet, so the flyout holds
        // only the question.
        bool offering = _rips.State == RipJobState.Offered;
        OfferPanel.Visibility = offering ? Visibility.Visible : Visibility.Collapsed;
        JobPanel.Visibility = offering ? Visibility.Collapsed : Visibility.Visible;

        if (offering)
        {
            RingArc.Stroke = new SolidColorBrush(Cyan);
            DiscGlyph.Foreground = new SolidColorBrush(Colors.White);
            FillRing(RingArc, 1);   // the disc is not read yet, so the ring is full
            TitleText.Text = _rips.Title;
            IconName.Set(ChipButton, Loc.Get("Rip_OfferTooltip"));

            // The question opens by itself. The chip is on the deck, so the user does not have to look for it. A
            // question nobody opened is a question nobody was asked.
            if (!_offerShown)
            {
                _offerShown = true;
                DispatcherQueue.TryEnqueue(() => StatusFlyout.ShowAt(ChipButton));
            }

            return;
        }

        _offerShown = false;

        bool failed = _rips.State == RipJobState.Failed;
        bool damaged = _rips.State == RipJobState.Damaged;

        // Gold is used for a rip that finished but came back with some tracks missing. The disc did not fail.
        // Saying it did would be as wrong as saying nothing happened.
        var ink = new SolidColorBrush(failed ? Red : damaged ? Gold : Cyan);
        RingArc.Stroke = ink;
        DiscGlyph.Foreground = failed || damaged ? ink : new SolidColorBrush(Colors.White);
        FillRing(RingArc, failed ? 1 : Math.Max(_rips.Fraction, MinArc));

        IconName.Set(ChipButton, failed
            ? Loc.Get("AddMusic_RipFailed")
            : damaged
                ? _rips.Status
                : Loc.Format("Rip_ChipTooltip", _rips.Title));

        TitleText.Text = string.IsNullOrWhiteSpace(_rips.Title) ? Loc.Get("Rip_ChipKicker") : _rips.Title;
        StatusText.Text = _rips.Status;
        RipBar.Value = _rips.Fraction;
        RipBar.Visibility = failed ? Visibility.Collapsed : Visibility.Visible;

        // The detail line carries a failure's message in red, and a short rip's tally in gold.
        DetailText.Foreground = new SolidColorBrush(failed ? Red : Gold);
        DetailText.Text = _rips.Detail;
        DetailText.Visibility = string.IsNullOrEmpty(_rips.Detail) ? Visibility.Collapsed : Visibility.Visible;

        TrackList.Visibility = _rips.Tracks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Skip and stop steer a rip that is still turning; once it has ended they have nothing to act on.
        var steering = _rips.CanSteer ? Visibility.Visible : Visibility.Collapsed;
        SkipButton.Visibility = steering;
        StopButton.Visibility = steering;

        // One button with two jobs. During a rip it cancels the rip, and its icon is a bin. After the rip
        // ends it clears the message, and its icon is a check, because there is nothing left to lose.
        ActionText.Text = Loc.Get(_rips.CanSteer ? "Common_Cancel" : "Rip_Dismiss");
        ActionIcon.Glyph = _rips.CanSteer ? "\uf1f8" : "\uf00c";
        ToolTipService.SetToolTip(ActionButton, _rips.CanSteer ? Loc.Get("Rip_CancelTooltip") : null);
    }

    /// <summary>Sets the ring to a fraction of a full circle, as plain points from the top, going clockwise.
    /// Code-built arc geometry draws nothing under Uno-Skia, but Polyline points are drawn as given.</summary>
    private static void FillRing(Microsoft.UI.Xaml.Shapes.Polyline line, double fraction)
    {
        var points = new PointCollection();
        foreach (var (x, y) in ProgressArc.Points(fraction, Centre, Centre, Radius, RingSegments))
            points.Add(new Point(x, y));
        line.Points = points;
    }

    // ── the disc that turned up by itself ────────────────────────────────────────────────────

    /// <summary>Not this one: the chip goes, and the drive is left alone until a different disc is put in.
    /// The disc is not ejected, because the user may have put it in to play it somewhere else.</summary>
    private void OfferLater_Click(object sender, RoutedEventArgs e)
    {
        StatusFlyout.Hide();
        _rips.CloseOffer();
    }

    /// <summary>Yes: the disc window opens on this disc, the same way as from Add music. The offer is removed
    /// from the deck first. After that the disc belongs to that window, and a chip that still asked about
    /// it would ask a question that has already been answered.</summary>
    private async void OfferRip_Click(object sender, RoutedEventArgs e)
    {
        StatusFlyout.Hide();
        _rips.CloseOffer();
        await RipDiscDialog.ShowForAsync(XamlRoot, DiscKind.Cd);
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RippedTrackRow row)
            _rips.TogglePreview(row);
    }

    /// <summary>Gives up on the track the drive is working on, and stays open. The point of the button is to
    /// watch the next track start.</summary>
    private void Skip_Click(object sender, RoutedEventArgs e) => _rips.SkipTrack();

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _rips.Stop();
        StatusFlyout.Hide();
    }

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        // During a rip this ends the rip, and it cannot be undone. After the rip, it removes the message.
        if (_rips.CanSteer)
            _rips.Cancel();
        else
            _rips.Dismiss();

        StatusFlyout.Hide();
    }
}

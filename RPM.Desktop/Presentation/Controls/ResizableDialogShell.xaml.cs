using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media.Animation;

namespace RPM.Desktop.Presentation.Controls;

/// <summary>A reusable resizable dialog card. It has a title (with an optional glyph and subtitle), a
/// darker body well, a footer (a leading part and a right-hand button group), and a corner resize grip.
/// Host it as the content of a ContentDialog with a transparent scrim, then call
/// <see cref="AttachResize"/>.</summary>
[ContentProperty(Name = nameof(Body))]
public sealed partial class ResizableDialogShell : UserControl
{
    private DragResizer? _resizer;

    public ResizableDialogShell() => InitializeComponent();

    // Titles are shown in lower case, with wide letter spacing, in the Cabin display face (the RPM header
    // look). The lower-casing uses the invariant culture, so a Turkish locale does not turn I into a
    // dotless i. The spacing is made by putting a thin space (U+2009) between the letters. CharacterSpacing
    // does nothing on Uno-Skia with this font.
    private string _title = "";
    public string Title
    {
        get => _title;
        set
        {
            _title = value ?? "";
            TitleText.Text = _title.ToLowerInvariant();
        }
    }

    /// <summary>The height of the title band. The default is 56. A few dialogs, such as the Add music
    /// source picker, need it taller.</summary>
    public double HeaderHeight { get => HeaderBand.Height; set => HeaderBand.Height = value; }

    public string TitleGlyph
    {
        get => TitleIcon.Glyph;
        set
        {
            var has = !string.IsNullOrEmpty(value);
            TitleIcon.Glyph = value ?? "";
            TitleIcon.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    public string Subtitle
    {
        get => SubtitleText.Text;
        set { SubtitleText.Text = value ?? ""; SubtitleText.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible; }
    }

    /// <summary>Whether the body scrolls (the default) or fills the card exactly.
    ///
    /// <para>A scrolling body is measured with unbounded height. That turns every star row inside it into
    /// an Auto row. A body laid out as panes (a waveform above a list, with a splitter between them) then
    /// grows past the card, and the whole body scrolls as one. The pane that should stay in place slides
    /// off the top. Set this to false for those bodies. The body is then laid out in the space the card
    /// really has.</para>
    /// </summary>
    private bool _scrollBody = true;
    public bool ScrollBody
    {
        get => _scrollBody;
        set
        {
            if (_scrollBody == value)
                return;
            _scrollBody = value;
            // A child can have only one parent. Release the presenter before adding it to another parent.
            if (value)
            {
                Well.Child = null;
                BodyScroll.Content = BodyPresenter;
                Well.Child = BodyScroll;
                Well.Padding = new Thickness(0);
            }
            else
            {
                BodyScroll.Content = null;
                Well.Child = BodyPresenter;
                // The side inset is on the scroller, so that its scroll bar can sit outside it. Without the
                // scroller, the well must carry the inset. Otherwise an unscrolled body runs to the card's
                // edges while the footer stays inset.
                Well.Padding = new Thickness(SideInset, 0, SideInset, 0);
            }
        }
    }

    public object? TitleTrailing { get => TitleTrailingPresenter.Content; set => TitleTrailingPresenter.Content = value; }

    /// <summary>A message across the card, flush under the header and edge to edge. See the BANNER region
    /// in the markup. It is null in every dialog that has nothing to say, and then it costs
    /// nothing.</summary>
    public object? Banner { get => BannerPresenter.Content; set => BannerPresenter.Content = value; }
    public object? Body { get => BodyPresenter.Content; set => BodyPresenter.Content = value; }

    /// <summary>A line under the body and above the buttons, outside the scroller. See the FOOTNOTE region
    /// in the markup. Use it for text that must stay readable wherever the body has been scrolled
    /// to.</summary>
    public object? Footnote { get => FootnotePresenter.Content; set => FootnotePresenter.Content = value; }
    public object? Footer { get => FooterPresenter.Content; set => FooterPresenter.Content = value; }
    public object? FooterLeading { get => FooterLeadingPresenter.Content; set => FooterLeadingPresenter.Content = value; }

    /// <summary>Shows the wizard step ribbon with these labels. If this is never called, there is no
    /// ribbon.</summary>
    public void SetSteps(params string[] labels)
    {
        StepsHost.SetSteps(labels);
        StepsHost.Visibility = labels.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyHeaderSpacing(labels.Length > 0);
    }

    /// <summary>The zero-based index of the active step in the ribbon.</summary>
    public int CurrentStep
    {
        get => StepsHost.Current;
        set => StepsHost.Current = value;
    }

    /// <summary>The rows carry their own top margins instead of a shared RowSpacing. So a hidden steps row
    /// collapses cleanly and leaves no gap behind.</summary>
    private void ApplyHeaderSpacing(bool stepsShown)
    {
        // The steps sit at the top of the content region. The body moves below them only when they are
        // shown.
        StepsHost.Margin = new Thickness(SideInset, 0, SideInset, 0);
        Well.Margin = new Thickness(0, stepsShown ? 14 : 0, 0, 0);
    }

    /// <summary>The card's side inset. Each row carries it itself, rather than the content region carrying
    /// it for all of them. See the note on that region in the XAML.</summary>
    private const double SideInset = 24;

    /// <summary>Connects the sizing and the grip to the owning dialog. Call it once, after
    /// InitializeComponent.</summary>
    public DragResizer AttachResize(ContentDialog dialog,
        double minWidth = 460, double minHeight = 360, double maxWidth = 720, double defaultHeight = 560,
        double? defaultWidth = null)
    {
        _resizer = new DragResizer(dialog, Card, minWidth, minHeight, maxWidth, defaultHeight, defaultWidth);
        Grip.Attach(_resizer);

        Card.Opacity = 0;
        dialog.Opened += (_, _) =>
        {
            var fade = new DoubleAnimation { To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(140)), EnableDependentAnimation = true };
            Storyboard.SetTarget(fade, Card);
            Storyboard.SetTargetProperty(fade, "Opacity");
            var sb = new Storyboard();
            sb.Children.Add(fade);
            Services.Shell.Storyboards.Play(sb);
        };
        return _resizer;
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace RPM.Desktop.Presentation.Main.Controls;

/// <summary>
/// Magnifier sweeping over a folder, with a caption and a running file count under it. It is shown in place
/// of the import pane's file list whenever the pane is busy: a source folder being scanned into the queue,
/// and the queue being filed into the collection.
///
/// <para>The control keeps no state of its own. The bar, the caption and the count are set from outside
/// (<see cref="Caption"/>, <see cref="CountText"/>, <see cref="ProgressValue"/>), so the same control
/// serves both jobs and the view model stays the single source of truth.</para>
///
/// <para>The storyboard repeats forever, so it must run ONLY while the control is visible. Loaded and
/// Unloaded fire when the control joins or leaves the visual tree, not when its visibility changes. This
/// control sits Collapsed on a page that is always mounted. A running Forever storyboard redraws every
/// frame and keeps the CPU busy, so the animation starts and stops with the Visibility property, not with
/// Loaded.</para>
/// </summary>
/// <summary>Which job the artwork is drawing.</summary>
public enum FolderProgressArt
{
    /// <summary>A folder being read: the magnifier sweeps across it.</summary>
    Search,

    /// <summary>A folder being filled: records drop into it one after another.</summary>
    Filing,
}

public sealed partial class FolderProgressAnimation : UserControl
{
    private Storyboard? _animation;

    /// <summary>The line above the bar, naming the work (for example "Moving selected files to your
    /// collection"). Empty hides the line, because a scan puts everything it needs in the count
    /// line.</summary>
    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(string), typeof(FolderProgressAnimation),
        new PropertyMetadata("", OnCaptionChanged));

    /// <summary>The line under the bar: "n / total files", or whatever the job counts in.</summary>
    public static readonly DependencyProperty CountTextProperty = DependencyProperty.Register(
        nameof(CountText), typeof(string), typeof(FolderProgressAnimation), new PropertyMetadata(""));

    public static readonly DependencyProperty ProgressMaxProperty = DependencyProperty.Register(
        nameof(ProgressMax), typeof(double), typeof(FolderProgressAnimation), new PropertyMetadata(1d));

    public static readonly DependencyProperty ProgressValueProperty = DependencyProperty.Register(
        nameof(ProgressValue), typeof(double), typeof(FolderProgressAnimation), new PropertyMetadata(0d));

    /// <summary>No total yet, so the bar sweeps instead of sitting empty. Set it back to false as soon as
    /// the job ends: an indeterminate bar runs its own Forever storyboard.</summary>
    public static readonly DependencyProperty IsIndeterminateProperty = DependencyProperty.Register(
        nameof(IsIndeterminate), typeof(bool), typeof(FolderProgressAnimation), new PropertyMetadata(false));

    /// <summary>Which artwork plays: a folder being searched, or a folder being filled. Set it once when
    /// the control is created. Changing it swaps both the artwork and the storyboard, even during a
    /// run.</summary>
    public static readonly DependencyProperty ArtProperty = DependencyProperty.Register(
        nameof(Art), typeof(FolderProgressArt), typeof(FolderProgressAnimation),
        new PropertyMetadata(FolderProgressArt.Search, OnArtChanged));

    public string Caption
    {
        get => (string)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    public string CountText
    {
        get => (string)GetValue(CountTextProperty);
        set => SetValue(CountTextProperty, value);
    }

    public double ProgressMax
    {
        get => (double)GetValue(ProgressMaxProperty);
        set => SetValue(ProgressMaxProperty, value);
    }

    public double ProgressValue
    {
        get => (double)GetValue(ProgressValueProperty);
        set => SetValue(ProgressValueProperty, value);
    }

    public bool IsIndeterminate
    {
        get => (bool)GetValue(IsIndeterminateProperty);
        set => SetValue(IsIndeterminateProperty, value);
    }

    public FolderProgressArt Art
    {
        get => (FolderProgressArt)GetValue(ArtProperty);
        set => SetValue(ArtProperty, value);
    }

    public FolderProgressAnimation()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        RegisterPropertyChangedCallback(VisibilityProperty, OnVisibilityChanged);
        SyncCaption();
        SyncArt();
    }

    private static void OnCaptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((FolderProgressAnimation)d).SyncCaption();

    private static void OnArtChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (FolderProgressAnimation)d;
        control.Stop();       // the old storyboard drives elements that are about to be hidden
        control.SyncArt();
        control.SyncPlayState();
    }

    private void SyncArt()
    {
        bool filing = Art == FolderProgressArt.Filing;
        SearchArt.Visibility = filing ? Visibility.Collapsed : Visibility.Visible;
        FilingArt.Visibility = filing ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Hides the caption line when there is no caption. An empty TextBlock still takes up the
    /// panel's spacing, which would leave the count sitting low under the magnifier.</summary>
    private void SyncCaption() =>
        CaptionText.Visibility = string.IsNullOrWhiteSpace(Caption) ? Visibility.Collapsed : Visibility.Visible;

    private void OnLoaded(object sender, RoutedEventArgs e) => SyncPlayState();

    private void OnUnloaded(object sender, RoutedEventArgs e) => Stop();

    private void OnVisibilityChanged(DependencyObject sender, DependencyProperty dp) => SyncPlayState();

    private void SyncPlayState()
    {
        if (Visibility == Visibility.Visible)
        {
            Begin();
        }
        else
        {
            Stop();
        }
    }

    private void Begin()
    {
        if (_animation != null)
        {
            return; // already running
        }

        var key = Art == FolderProgressArt.Filing ? "FileDropAnimation" : "MagnifySearchAnimation";
        if (Resources[key] is Storyboard sb)
        {
            _animation = sb;
            _animation.Begin();
        }
    }

    private void Stop()
    {
        _animation?.Stop();
        _animation = null;
    }
}

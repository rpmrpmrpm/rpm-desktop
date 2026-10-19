using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// Resizes a dialog that has a transparent full-screen scrim. The <see cref="ContentDialog"/> is the
/// scrim, and the visible card is an inner element (a layered glass panel). This class owns all the
/// sizing and the corner grip. The grip is driven by the pointer, because Thumb.DragDelta does not
/// fire inside the dialog popup on Uno.
/// </summary>
public sealed class DragResizer
{
    private readonly ContentDialog _dialog;
    private readonly FrameworkElement _card;
    private readonly double _minWidth, _minHeight, _maxWidth, _defaultHeight, _defaultWidth;
    private const double ParentWindowMargin = 40;

    private bool _resizing;
    private Point _resizeStart;
    private double _resizeStartWidth, _resizeStartHeight;

    /// <param name="defaultWidth">The width the dialog opens at. If null, the dialog fills the window up to
    /// <paramref name="maxWidth"/>. That suits a dialog that is a table or a picture. For a dialog with a
    /// fixed layout, whose fields and columns were arranged for one width, give a value. The dialog then
    /// opens at that width on any screen. The grip can still pull it out to <paramref
    /// name="maxWidth"/>.</param>
    public DragResizer(ContentDialog dialog, FrameworkElement card,
        double minWidth = 460, double minHeight = 360, double maxWidth = 720, double defaultHeight = 640,
        double? defaultWidth = null)
    {
        _dialog = dialog;
        _card = card;
        _minWidth = minWidth;
        _minHeight = minHeight;
        _maxWidth = maxWidth;
        _defaultHeight = defaultHeight;
        _defaultWidth = Math.Clamp(defaultWidth ?? maxWidth, minWidth, maxWidth);

        ApplyDialogSize(_defaultWidth, defaultHeight);
        _dialog.Loaded += (_, _) => SizeToParentWindow();
    }

    public void SizeToParentWindow()
    {
        if (_dialog.XamlRoot is not { } root) return;
        var avail = root.Size;
        var w = Math.Clamp(Math.Min(_defaultWidth, avail.Width - ParentWindowMargin * 2), _minWidth, _maxWidth);
        var h = Math.Clamp(_defaultHeight, _minHeight, Math.Max(_minHeight, avail.Height - ParentWindowMargin * 2));
        ApplyDialogSize(w, h);
    }

    private void ApplyDialogSize(double width, double height)
    {
        _card.MinWidth = _minWidth;
        _card.MinHeight = _minHeight;
        _card.Width = width;
        _card.Height = height;
    }

    public void GripPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border grip) return;
        _resizing = true;
        _resizeStart = e.GetCurrentPoint(null).Position;
        _resizeStartWidth = _card.ActualWidth;
        _resizeStartHeight = _card.ActualHeight;
        grip.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    public void GripMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_resizing) return;
        var pos = e.GetCurrentPoint(null).Position;
        var dx = pos.X - _resizeStart.X;
        var dy = pos.Y - _resizeStart.Y;
        var winW = _dialog.XamlRoot is { } r1 ? r1.Size.Width - ParentWindowMargin * 2 : _resizeStartWidth;
        var winH = _dialog.XamlRoot is { } r2 ? r2.Size.Height - ParentWindowMargin * 2 : _resizeStartHeight;
        var maxW = Math.Min(_maxWidth, winW);
        var w = Math.Clamp(_resizeStartWidth + dx, _minWidth, Math.Max(_minWidth, maxW));
        var h = Math.Clamp(_resizeStartHeight + dy, _minHeight, Math.Max(_minHeight, winH));
        ApplyDialogSize(w, h);
    }

    public void GripReleased(object sender, PointerRoutedEventArgs e) { EndResize(sender, e.Pointer); e.Handled = true; }
    public void GripCaptureLost(object sender, PointerRoutedEventArgs e) => EndResize(sender, e.Pointer);

    private void EndResize(object sender, Pointer pointer)
    {
        if (sender is Border grip) grip.ReleasePointerCapture(pointer);
        _resizing = false;
    }
}

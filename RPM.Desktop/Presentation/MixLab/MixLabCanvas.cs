#if DEBUG
#if RPM_SKIA
// The mix lab is a Debug tool for the Skia head only. Its canvas is Uno's SKCanvasElement, which the
// WinUI head cannot reference. So a Debug build of the WinUI head would not compile.
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using SkiaSharp;
using Uno.WinUI.Graphics2DSK;
using Windows.Foundation;
using RPM.Desktop.Presentation.Common;

namespace RPM.Desktop.Presentation.MixLab;

/// <summary>A Skia surface that passes its render to a delegate. Every MixLab control draws on this
/// canvas, so a platform fix is made once and not once per copy.</summary>
internal sealed partial class MixLabSurface : SKCanvasElement
{
    public Action<SKCanvas, Size>? Draw { get; init; }

    protected override void RenderOverride(SKCanvas canvas, Size area) => Draw?.Invoke(canvas, area);
}

/// <summary>The transparent sheet that takes the pointer over a MixLab canvas. A bare canvas is not a
/// hit-test target, so it needs this sheet. See <see cref="Dialogs.Controls.WaveformEditor"/>, which
/// does the same.</summary>
internal sealed partial class MixLabInputLayer : BorderBase
{
    private InputSystemCursorShape _shape = InputSystemCursorShape.Arrow;

    public MixLabInputLayer()
    {
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        IsTabStop = false;
    }

    public void SetCursor(InputSystemCursorShape shape)
    {
        if (shape == _shape)
            return;
        _shape = shape;
        ProtectedCursor = InputSystemCursor.Create(shape);
    }
}
#endif
#endif

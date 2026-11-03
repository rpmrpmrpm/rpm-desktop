using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using System.Numerics;

namespace RPM.Desktop.Presentation.Main;

/// <summary>
/// The import workbench sliding out over the row, and back into its column.
///
/// <para>The card is laid out ONCE, across the whole row, and what moves is a clip on its far edge: from
/// the width its column had to the width of the row on the way out, and back on the way in. The same move
/// as <see cref="PaneGlide"/>, for the same reason: a clip runs on the compositor, where the drawing
/// thread's own work cannot make it stutter, and nothing under it is laid out per frame. The library
/// beneath is not touched at all.</para>
///
/// <para>The clip is a rounded rectangle, so the moving edge keeps the card's corners, and its width is an
/// expression over one animated value, so a move turned round half way (Import pressed twice) carries on
/// from where the edge is rather than from either end.</para>
///
/// <para>On the Skia head nothing animates: the card is simply there, or not, and the landing is
/// immediate.</para>
/// </summary>
internal sealed class ImportCover
{
    /// <summary>Which move is the current one. A move overtaken by a newer one does not land.</summary>
    private int _turn;

#if RPM_WINUI
    /// <summary>Carries <c>W</c>, how much of the card is showing, measured from its left edge.</summary>
    private CompositionPropertySet? _driver;
    private Visual? _visual;
    private bool _clipped;
#endif

    /// <summary>The card has just been stretched over the row; it slides out to it from
    /// <paramref name="from"/>, the width its column gave it.</summary>
    /// <param name="to">The row's width, which the card is being stretched to. Passed in rather than read off
    /// the card, which has not been laid out at that width yet.</param>
    public void Open(FrameworkElement card, double from, double to, double ms)
    {
        var turn = ++_turn;
#if RPM_WINUI
        Move(card, from, to, ms, () =>
        {
            if (turn == _turn)
                Unclip();
        });
#endif
    }

    /// <summary>The card slides back to <paramref name="to"/>, its column's width, and
    /// <paramref name="landed"/> runs once it is there, unless another move has started since.</summary>
    public void Close(FrameworkElement card, double from, double to, double ms, Action landed)
    {
        var turn = ++_turn;
#if RPM_WINUI
        Move(card, from, to, ms, () =>
        {
            if (turn != _turn)
                return;
            landed();
            Unclip();
        });
#else
        landed();
#endif
    }

    /// <summary>Whatever is moving stops where it is, and the card is shown whole. For a pane move that
    /// takes the card over.</summary>
    public void Stop()
    {
        _turn++;
#if RPM_WINUI
        _driver?.StopAnimation("W");
        Unclip();
#endif
    }

#if RPM_WINUI
    private void Move(FrameworkElement card, double from, double to, double ms, Action done)
    {
        var visual = ElementCompositionPreview.GetElementVisual(card);
        var compositor = visual.Compositor;

        var driver = _driver ??= compositor.CreatePropertySet();

        // A move turned round carries on from the edge where it is: the compositor supplies the live value
        // (see "this.StartingValue" below). A fresh move starts from the column's width.
        if (!_clipped || !ReferenceEquals(_visual, visual))
        {
            driver.InsertScalar("W", (float)from);
            Clip(compositor, card, visual, driver);
        }

        var ramp = compositor.CreateScalarKeyFrameAnimation();
        ramp.Duration = TimeSpan.FromMilliseconds(ms);
        ramp.InsertExpressionKeyFrame(0f, "this.StartingValue");
        ramp.InsertKeyFrame(1f, (float)to, PaneGlide.Ease(compositor));

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        driver.StartAnimation("W", ramp);
        batch.End();
        batch.Completed += (_, _) => done();
    }

    /// <summary>A rounded clip whose width follows <c>W</c>, never wider than the card itself.</summary>
    private void Clip(Compositor compositor, FrameworkElement card, Visual visual, CompositionPropertySet driver)
    {
        var radius = card is Microsoft.UI.Xaml.Controls.Border border ? (float)border.CornerRadius.TopLeft : 0f;

        var shape = compositor.CreateRoundedRectangleGeometry();
        shape.CornerRadius = new Vector2(radius, radius);

        var size = compositor.CreateExpressionAnimation("Vector2(Min(d.W, v.Size.X), v.Size.Y)");
        size.SetReferenceParameter("d", driver);
        size.SetReferenceParameter("v", visual);
        shape.StartAnimation("Size", size);

        visual.Clip = compositor.CreateGeometricClip(shape);
        _visual = visual;
        _clipped = true;
    }

    private void Unclip()
    {
        if (_visual is { } visual)
            visual.Clip = null;
        _clipped = false;
    }
#endif
}

using System.Numerics;

using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

using Windows.Foundation;
using Windows.UI;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// A blurred shadow under a line of text: an empty element declared behind the text carries it.
///
/// <code>
/// &lt;Grid&gt;
///   &lt;Border common:TextShade.Under="TitleFace" common:TextShade.Ink="#B3000000"/&gt;
///   &lt;TextBlock x:Name="TitleFace" Text="..." Style="{StaticResource FeatureTitle}"/&gt;
/// &lt;/Grid&gt;
/// </code>
///
/// <para><b>Why a second element and not a property on the TextBlock.</b> The shadow must be drawn
/// BEHIND the glyphs. A child visual that hangs off an element is drawn IN FRONT of that element's own
/// content. So the thing that carries the shadow is a sibling, declared before the text. Its place in
/// the z-order is stated in the markup.</para>
///
/// <para><b>Why not ThemeShadow.</b> <c>UIElement.Shadow</c> casts a shadow from the element's BOX.
/// Under a line of type that is a grey rectangle. A shadow shaped like the letters needs the letters as
/// a mask. So the text's own visual is rendered to a surface, that surface becomes the <see
/// cref="DropShadow.Mask"/>, and the compositor blurs it.</para>
///
/// <para><b>Why not a second TextBlock offset by a point.</b> That is the fallback where no blur is
/// available. It gives a hard copy, which at small sizes looks like a printing fault. It also needs the
/// Text binding to be written twice for each line.</para>
///
/// <para><b>What happens where the compositor cannot do it.</b> Nothing is drawn. <see cref="Attach"/>
/// returns null, and the text is drawn with no shadow. That is the same fallback that <see
/// cref="ScrollHem"/> uses.</para>
///
/// <para><b>On the Skia heads it is decided when the app is built.</b> Uno's compositor has no
/// <c>DropShadow</c>, and every member this uses is marked not implemented there. A shadow built on
/// Skia would be a sprite and a surface that draw nothing, rebuilt on every size change. So the
/// compositor code is compiled for WinUI only, and
/// <see cref="Attach"/> returns null on the other heads.</para>
/// </summary>
public static partial class TextShade
{
    /// <summary>
    /// The name of the TextBlock this element shadows. Setting it is what turns the shadow on.
    /// </summary>
    /// <remarks>
    /// A name rather than the element itself, because this is written inside a DataTemplate: the
    /// template's elements share one namescope, so the host can find its text by name once it is in the
    /// tree, and a <c>{Binding ElementName}</c> would have to resolve before that.
    /// </remarks>
    public static readonly DependencyProperty UnderProperty = DependencyProperty.RegisterAttached(
        "Under", typeof(string), typeof(TextShade), new PropertyMetadata(null, OnUnderChanged));

    public static string GetUnder(FrameworkElement host) => (string)host.GetValue(UnderProperty);

    public static void SetUnder(FrameworkElement host, string value) => host.SetValue(UnderProperty, value);

    /// <summary>How far the shadow is blurred, as a share of the line's own height.
    /// </summary>
    /// <remarks>A share and not a fixed radius, because one number here covers several sizes of type.
    /// A fixed radius that looks like depth under a 24 point title is a smudge under an 11 point
    /// kicker. The three lines on a featured card are 29, 16 and 13 points of line height. The default
    /// is 30 per cent.
    ///
    /// <para>It is read at every measure rather than once at attach, so a style change takes
    /// effect.</para>
    /// </remarks>
    public static readonly DependencyProperty BlurProperty = DependencyProperty.RegisterAttached(
        "Blur", typeof(double), typeof(TextShade), new PropertyMetadata(0.30));

    public static double GetBlur(FrameworkElement host) => (double)host.GetValue(BlurProperty);

    public static void SetBlur(FrameworkElement host, double value) => host.SetValue(BlurProperty, value);

    /// <summary>The shadow's colour, including its alpha. The alpha is taken off the colour and set as
    /// the shadow's own opacity. A DropShadow has both, and two places to set how dark the shadow is
    /// would multiply together.</summary>
    public static readonly DependencyProperty InkProperty = DependencyProperty.RegisterAttached(
        "Ink", typeof(Color), typeof(TextShade), new PropertyMetadata(Color.FromArgb(0xB3, 0, 0, 0)));

    public static Color GetInk(FrameworkElement host) => (Color)host.GetValue(InkProperty);

    public static void SetInk(FrameworkElement host, Color value) => host.SetValue(InkProperty, value);

    /// <summary>How far the shadow falls below the text, in points. This is small on purpose. The blur
    /// is what makes it read as a shadow. A large drop under a blur that wide makes the text look as if
    /// it floats.</summary>
    public static readonly DependencyProperty DropProperty = DependencyProperty.RegisterAttached(
        "Drop", typeof(double), typeof(TextShade), new PropertyMetadata(1.0));

    public static double GetDrop(FrameworkElement host) => (double)host.GetValue(DropProperty);

    public static void SetDrop(FrameworkElement host, double value) => host.SetValue(DropProperty, value);

    /// <summary>The live shadow, kept on the host so it can be torn down when the host leaves.</summary>
    private static readonly DependencyProperty LiveProperty = DependencyProperty.RegisterAttached(
        "Live", typeof(object), typeof(TextShade), new PropertyMetadata(null));

    private static void OnUnderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement host)
            return;

        // Only hooked here, never built. Other properties may still be being parsed, because XAML sets
        // attached properties in the order they are written. The text this shadows may not be in the
        // tree yet. Loaded is the first moment that all the values and both elements exist.
        host.Loaded -= OnLoaded;
        host.Unloaded -= OnUnloaded;

        if (e.NewValue is not string { Length: > 0 })
        {
            Release(host);
            return;
        }

        host.Loaded += OnLoaded;
        host.Unloaded += OnUnloaded;

        // A host that is already loaded does not raise Loaded again. This happens when a template is
        // reused. A template already in the tree can have this property set again, when the card it
        // holds is replaced.
        if (host.IsLoaded)
            OnLoaded(host, new RoutedEventArgs());
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement host)
            return;

        Release(host);

        if (GetUnder(host) is not { Length: > 0 } name || host.FindName(name) is not TextBlock face)
            return;

        if (Attach(host, face) is { } live)
            host.SetValue(LiveProperty, live);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement host)
            Release(host);
    }

    private static void Release(FrameworkElement host)
    {
        if (host.GetValue(LiveProperty) is IDisposable live)
            live.Dispose();

        host.SetValue(LiveProperty, null);
    }

    /// <summary>
    /// Builds one shadow, or answers null where the compositor will not have it.
    /// </summary>
    private static IDisposable? Attach(FrameworkElement host, TextBlock face)
    {
#if !RPM_WINUI
        // Uno's compositor has no DropShadow, so nothing can be built here. See the class summary.
        return null;
#else
        try
        {
            var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;

            var copy = compositor.CreateVisualSurface();
            copy.SourceVisual = ElementCompositionPreview.GetElementVisual(face);

            var pixels = compositor.CreateSurfaceBrush(copy);

            // The surface is drawn at the text's own size, so nothing is scaled and no sampler is
            // needed. The compositor has no bicubic mode. CompositionBitmapInterpolationMode stops at
            // linear and its mip variants. The smoothness comes from the Gaussian blur below, which
            // runs at the surface's own resolution.
            pixels.Stretch = CompositionStretch.None;
            pixels.HorizontalAlignmentRatio = 0;
            pixels.VerticalAlignmentRatio = 0;

            // Set on its own, and any failure is caught on its own. Linear is supported everywhere. The
            // mip variant is the smoothest mode, but it is not implemented on every backend. Losing the
            // whole shadow over it would be worse than losing the last of the smoothing.
            pixels.BitmapInterpolationMode = CompositionBitmapInterpolationMode.Linear;
            try
            {
                pixels.BitmapInterpolationMode =
                    CompositionBitmapInterpolationMode.MagLinearMinLinearMipLinear;
            }
            catch
            {
                // Linear, which was already set.
            }

            var shadow = compositor.CreateDropShadow();
            shadow.Mask = pixels;

            var sprite = compositor.CreateSpriteVisual();

            // No brush on the sprite: it draws its shadow and nothing else. A brush here would be a
            // second copy of the text standing over the real one.
            sprite.Shadow = shadow;
            ElementCompositionPreview.SetElementChildVisual(host, sprite);

            var live = new Shade(host, face, copy, pixels, shadow, sprite);
            live.Measure();
            return live;
        }
        catch
        {
            return null;
        }
#endif
    }

#if RPM_WINUI

    /// <summary>
    /// One live shadow: the surface the text is rendered to, the shadow that blurs it, the sprite that
    /// draws it, and the two size changes that keep all three fitting the text.
    /// </summary>
    private sealed partial class Shade : IDisposable
    {
        private readonly FrameworkElement _host;
        private readonly TextBlock _face;
        private readonly CompositionVisualSurface _copy;
        private readonly CompositionSurfaceBrush _pixels;
        private readonly DropShadow _shadow;
        private readonly SpriteVisual _sprite;
        private readonly long _watchingVisibility;

        public Shade(
            FrameworkElement host, TextBlock face, CompositionVisualSurface copy,
            CompositionSurfaceBrush pixels, DropShadow shadow, SpriteVisual sprite)
        {
            _host = host;
            _face = face;
            _copy = copy;
            _pixels = pixels;
            _shadow = shadow;
            _sprite = sprite;

            _face.SizeChanged += OnChanged;
            _host.SizeChanged += OnChanged;

            // The third line of a featured card is two TextBlocks in one cell. Only one is visible at a
            // time, and which one changes with the card. Changing Visibility raises no SizeChanged, so this
            // watches Visibility as well. Without it, the shadow of a hidden line would stay on the card.
            _watchingVisibility = _face.RegisterPropertyChangedCallback(
                UIElement.VisibilityProperty, (_, _) => Measure());
        }

        private void OnChanged(object sender, SizeChangedEventArgs e) => Measure();

        /// <summary>Fits the surface, the sprite and the blur to the text as it is now.
        /// </summary>
        /// <remarks>
        /// <b>A surface with no pixels must not be drawn.</b> A mask brush given a surface of zero size
        /// makes Skia copy memory from nothing, in the middle of a frame. See <see
        /// cref="BandSurface"/>, which states the same rule for the dissolve bands. So a line with no
        /// text, or one that is collapsed, takes the sprite out of the draw and writes nothing to the
        /// surface. The last good size stays on the surface, unused, until the line comes back. Both
        /// states are normal here. A station's card has no kicker, and the hype and the act's name take
        /// turns in one cell.
        /// </remarks>
        public void Measure()
        {
            try
            {
                var width = (float)_face.ActualWidth;
                var height = (float)_face.ActualHeight;

                if (width <= 0 || height <= 0 || _face.Visibility != Visibility.Visible)
                {
                    _sprite.IsVisible = false;
                    return;
                }

                _copy.SourceSize = new Vector2(width, height);
                _sprite.Size = new Vector2(width, height);

                // The text is not always in the host's corner. For example, the act's name is centred
                // in a cell taller than it is. A shadow pinned to the corner would sit above its line.
                // So the position is read from the layout, not worked out from the alignment. One
                // answer then covers every way a line can be placed.
                var at = _face.TransformToVisual(_host).TransformPoint(new Point(0, 0));
                _sprite.Offset = new Vector3((float)at.X, (float)at.Y, 0);

                var ink = GetInk(_host);
                _shadow.Color = Color.FromArgb(0xFF, ink.R, ink.G, ink.B);
                _shadow.Opacity = ink.A / 255f;
                _shadow.BlurRadius = (float)Math.Max(0, GetBlur(_host) * LineHeight(_face));
                _shadow.Offset = new Vector3(0, (float)GetDrop(_host), 0);

                _sprite.IsVisible = true;
            }
            catch
            {
                // A shadow that cannot be measured is a shadow that is not drawn. The text is unaffected.
                try { _sprite.IsVisible = false; } catch { }
            }
        }

        /// <summary>The line's height: what the block was told, or what its size implies where it was
        /// told nothing. TextBlock.LineHeight is zero until it is set, which is the usual case. Only
        /// the title on a featured card sets one.</summary>
        private static double LineHeight(TextBlock face)
            => face.LineHeight > 0 ? face.LineHeight : face.FontSize * 1.2;

        public void Dispose()
        {
            _face.SizeChanged -= OnChanged;
            _host.SizeChanged -= OnChanged;
            _face.UnregisterPropertyChangedCallback(UIElement.VisibilityProperty, _watchingVisibility);

            try
            {
                ElementCompositionPreview.SetElementChildVisual(_host, null);
            }
            catch
            {
                // This only throws when the host is already gone, and that is the wanted state.
            }

            _sprite.Dispose();
            _shadow.Dispose();
            _pixels.Dispose();
            _copy.Dispose();
        }
    }
#endif
}

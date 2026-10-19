using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.Controls;

/// <summary>The thin edge that both drag grips are drawn with, at rest and under the pointer.
///
/// <para>It is one pill shape, used in two directions. It lies flat along the bottom of the artwork (the
/// shell's band splitter), and on its side between the panels (the library's). Each is a frosted-glass fill
/// behind a thin edge line. Only the edge line changes. It is barely visible until the pointer is over the
/// grip or a drag is in progress. Then it is bright white.</para>
///
/// <para>The brushes come from the app's resources, not from each page. They are read in one place, so the
/// two grips always look the same. Two grips that light up differently would look like two controls, not
/// one gesture in two directions.</para>
/// </summary>
internal static class GripEdge
{
    public static Brush Brush(bool lit)
        => (Brush)Application.Current.Resources[lit ? "GripEdgeHoverBrush" : "GripEdgeBrush"];
}

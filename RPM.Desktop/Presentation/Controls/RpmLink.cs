using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Controls;

/// <summary>A line of text that acts, such as "Show more", "Read on Wikipedia", or a jump to a section of a
/// settings page. It is the app's fourth button. The three in <see cref="RpmButton"/> are chrome. This one
/// is a word in the text it sits in.
///
/// <para>It looks like a link because it acts like one. It has no box and no wash under the pointer, and it
/// shows the hand cursor. The cursor is the reason this is a type at all, and not just a Style.
/// <c>ProtectedCursor</c> is protected, so only a control can set its own cursor. That is also why
/// <see cref="PanelSplitter"/> and <see cref="RowSplitter"/> exist.</para>
///
/// <para>The look comes from the implicit style in Themes/Generic.xaml: cyan, transparent, and a template
/// that dims on press instead of painting a plate. A stock Button cannot do the second part. Its template
/// reads <c>ButtonBackgroundPointerOver</c> by key. So a Style with a transparent Background still lights
/// up under the pointer.</para>
/// </summary>
public partial class RpmLink : Button
{
    public RpmLink()
    {
        DefaultStyleKey = typeof(RpmLink);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
    }
}

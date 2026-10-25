using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// A line in Pegasus that the TV remote can stand on: a track on a record or list page, or a row in the
/// drawer. It takes the focus. When it is built, it is given what OK does on it and what holding OK (or
/// the Menu key) does on it.
///
/// <para>It handles no pointer input. The mouse is left to whatever is inside it: the drawer's rows handle
/// their own press, drag and swipe on a Border. A Button in a focusable host would take the pointer away
/// from them. The keys arrive through the shell (ShellPage.TakePegasusKey), which passes OK, a held OK and
/// the Menu key to the host (PegasusPlayer.OkDown, MenuKey).</para>
///
/// <para>All three actions are constructor arguments with no default, so a line cannot be built without
/// saying what OK does, what a held OK does, and what Left and Right do. A line with no menu passes null.
/// The host then presses it when the key goes down, and does not wait to see whether the key is held. A
/// line that Left and Right move off passes null for the turn.</para>
/// </summary>
public sealed partial class RemoteLine : ContentControl
{
    private readonly Action _press;
    private readonly Func<MenuFlyout?>? _menu;
    private readonly Func<int, bool>? _turn;

    /// <param name="content">What the line shows, and what takes the pointer.</param>
    /// <param name="press">What OK does. It does the same thing as a click on the line.</param>
    /// <param name="menu">Builds the line's menu without showing it, for a held OK or the Menu key. Null for
    /// a line with no menu. The menu is returned unshown so the host can attach to it before it opens.</param>
    /// <param name="turn">What Left (-1) and Right (1) do while the line has the focus. Where they step the
    /// line, they do not move the focus off it. The home banner uses this to step through its cards. It
    /// returns whether the line moved. False means the end of the line, and the host bounces. Null for a
    /// line that Left and Right move off.</param>
    public RemoteLine(UIElement content, Action press, Func<MenuFlyout?>? menu, Func<int, bool>? turn)
    {
        _press = press;
        _menu = menu;
        _turn = turn;

        Content = content;

        // ContentControl's default style turns IsTabStop off and aligns its content to the top left.
        IsTabStop = true;
        UseSystemFocusVisuals = false;   // the host draws one ring over everything instead
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    /// <summary>
    /// A line around a button that already exists, for a button that should keep its look and its mouse
    /// press but gain a menu for the remote: a card on the album wall, a quick song on home. The line takes
    /// the button's place in its panel, if it stands in one, with the button inside it. The button stops
    /// being a stop for the keys, so the line takes the focus, and OK, a held OK and the Menu key all arrive
    /// on one element. A click with the mouse still lands on the button.
    ///
    /// <para>The line takes the button's alignment and corner radius, so it stands where the button stood,
    /// and the button's mark for the focus look (FocusLift.Part), so a card stays a card. A caller that
    /// changes the button's corner later writes it to the line too.</para>
    ///
    /// <para>A button that is the root of a DataTemplate has no panel to stand in. Such a template puts a
    /// Grid around the button, and the line is made when the button is loaded (see HomeView and
    /// PegasusChrome.Library).</para>
    /// </summary>
    /// <param name="press">What OK does: what a click on the button does.</param>
    /// <param name="menu">Builds the menu for a held OK or the Menu key, as in the constructor.</param>
    public static RemoteLine Around(Button key, Action press, Func<MenuFlyout?> menu)
    {
        var panel = key.Parent as Panel;
        var at = panel?.Children.IndexOf(key) ?? -1;
        if (at >= 0)
            panel!.Children.RemoveAt(at);

        key.IsTabStop = false;
        var line = new RemoteLine(key, press, menu, turn: null)
        {
            HorizontalAlignment = key.HorizontalAlignment,
            VerticalAlignment = key.VerticalAlignment,
            CornerRadius = key.CornerRadius,
        };
        FocusLift.SetPart(line, FocusLift.GetPart(key));

        if (at >= 0)
            panel!.Children.Insert(at, line);
        return line;
    }

    /// <summary>Whether a held OK or the Menu key opens anything here.</summary>
    public bool HasMenu => _menu is not null;

    /// <summary>Does what OK does on this line.</summary>
    public void Press() => _press();

    /// <summary>The line's menu, built but not shown. Null if the line has no menu, or if the menu cannot
    /// be built now, for example because the track is gone.</summary>
    public MenuFlyout? BuildMenu() => _menu?.Invoke();

    /// <summary>Whether Left and Right step this line rather than move the focus off it.</summary>
    public bool Turns => _turn is not null;

    /// <summary>Steps the line one way (-1 for Left, 1 for Right). Returns false if the line did not move,
    /// and always false for a line that does not turn.</summary>
    public bool Turn(int way) => _turn?.Invoke(way) == true;
}

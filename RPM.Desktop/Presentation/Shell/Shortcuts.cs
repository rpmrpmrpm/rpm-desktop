using Microsoft.UI.Xaml.Controls;
using RPM.Desktop.Presentation.Grids;
using Windows.System;

namespace RPM.Desktop.Presentation.Shell;

/// <summary>What a keyboard shortcut asks for.</summary>
internal enum Shortcut
{
    None,
    Previous,
    Next,
    VolumeUp,
    VolumeDown,
    Search,
}

/// <summary>
/// The window's keyboard shortcuts besides the space bar, decided once for the whole window. The
/// modifier is Cmd on a Mac and Ctrl on Windows. Left is the previous track, Right is the next track,
/// Up and Down change the volume, and F goes to the search field of the screen in front. These are the
/// same keys that the Mac's Music app and Spotify use.
///
/// <para>The shortcuts need exactly that one modifier. Shift, Option or Alt together with it is another
/// shortcut, or a selection being extended in a field. AltGr on a Windows keyboard is Ctrl and Alt
/// together, so a character typed with it is never read as a shortcut.</para>
///
/// <para>The route decides who owns the key, as it does for <see cref="SpaceBar"/>. A text field keeps
/// the arrows, which with the modifier move the caret by a word or to the end of the line. The library
/// table keeps Up and Down with Ctrl, which move its focused row without changing the selection
/// (<c>GridSelection.KeyTo</c>). That only happens on Windows, because on a Mac the table reads the
/// Control key and the shortcut is Cmd. No control claims F, because no field answers Cmd+F or
/// Ctrl+F.</para>
///
/// <para>The result depends only on the key, the modifiers and the types, so <c>ShortcutsTests</c> can
/// test the rule without a window.</para>
/// </summary>
internal static class Shortcuts
{
    /// <summary>The controls that type text. These are base types, matched by assignment, as in
    /// <see cref="SpaceBar"/>.</summary>
    private static readonly Type[] TextFields =
    [
        typeof(TextBox),
        typeof(PasswordBox),
        typeof(RichEditBox),
        typeof(AutoSuggestBox),
    ];

    /// <summary>The one modifier the shortcuts are held with: Cmd on a Mac, Ctrl everywhere else.</summary>
    public static Modifier Primary(bool mac) => mac ? Modifier.Command : Modifier.Control;

    /// <summary>
    /// The shortcut a key going down is, given the modifiers held, the platform, and the focused
    /// control's type and its parents' in order. <see cref="Shortcut.None"/> when it is not one, or when
    /// something on the route keeps the key.
    /// </summary>
    public static Shortcut For(VirtualKey key, Modifier held, bool mac, IEnumerable<Type> route)
    {
        var primary = Primary(mac);
        if (held != primary)
            return Shortcut.None;

        var shortcut = KeyFor(key);
        if (shortcut is Shortcut.None or Shortcut.Search)
            return shortcut;

        var types = route.ToList();
        if (types.Any(t => TextFields.Any(f => f.IsAssignableFrom(t))))
            return Shortcut.None;

        if (shortcut is Shortcut.VolumeUp or Shortcut.VolumeDown
            && primary == Modifier.Control
            && types.Any(typeof(RpmGrid).IsAssignableFrom))
            return Shortcut.None;

        return shortcut;
    }

    /// <summary>Whether the key is one of the five, checked before anything else. The caller reads the
    /// modifiers only after this returns true: on a Mac that read writes a ring line whenever Uno's key
    /// table disagrees with the keyboard, so it must not run on every letter typed.</summary>
    public static bool IsShortcutKey(VirtualKey key) => KeyFor(key) != Shortcut.None;

    private static Shortcut KeyFor(VirtualKey key) => key switch
    {
        VirtualKey.Left => Shortcut.Previous,
        VirtualKey.Right => Shortcut.Next,
        VirtualKey.Up => Shortcut.VolumeUp,
        VirtualKey.Down => Shortcut.VolumeDown,
        VirtualKey.F => Shortcut.Search,
        _ => Shortcut.None,
    };

    /// <summary>Whether the shortcut acts again while its key is held and the keyboard repeats it. The
    /// volume keeps stepping, as the volume keys of a keyboard do. A track change or the search acts once
    /// per press.</summary>
    public static bool ActsOnRepeat(Shortcut shortcut) => shortcut is Shortcut.VolumeUp or Shortcut.VolumeDown;
}

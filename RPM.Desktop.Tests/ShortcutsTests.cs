using Microsoft.UI.Xaml.Controls;
using RPM.Desktop.Presentation.Grids;
using RPM.Desktop.Presentation.Main;
using RPM.Desktop.Presentation.Shell;
using RPM.Desktop.Services.Platform;
using Windows.System;

namespace RPM.Desktop.Tests;

/// <summary>
/// The keyboard shortcuts: Cmd on a Mac and Ctrl on Windows, with Left, Right, Up, Down and F. Types and a
/// modifier set, as <see cref="SpaceBarTests"/> holds the space bar to.
/// </summary>
public class ShortcutsTests
{
    private static readonly Type[] Library = [typeof(MainPage), typeof(ShellPage)];

    [Theory]
    [InlineData(VirtualKey.Left, Shortcut.Previous)]
    [InlineData(VirtualKey.Right, Shortcut.Next)]
    [InlineData(VirtualKey.Up, Shortcut.VolumeUp)]
    [InlineData(VirtualKey.Down, Shortcut.VolumeDown)]
    [InlineData(VirtualKey.F, Shortcut.Search)]
    [InlineData(VirtualKey.G, Shortcut.None)]
    public void Cmd_on_a_Mac_and_Ctrl_on_Windows(VirtualKey key, object shortcut)
    {
        var expected = (Shortcut)shortcut;
        Assert.Equal(expected, Shortcuts.For(key, Modifier.Command, mac: true, Library));
        Assert.Equal(expected, Shortcuts.For(key, Modifier.Control, mac: false, Library));
    }

    /// <summary>The other platform's modifier is not the shortcut: Ctrl+Right on a Mac and the Windows
    /// key with Right on Windows are left alone.</summary>
    [Fact]
    public void The_other_platforms_modifier_is_not_a_shortcut()
    {
        Assert.Equal(Shortcut.None, Shortcuts.For(VirtualKey.Right, Modifier.Control, mac: true, Library));
        Assert.Equal(Shortcut.None, Shortcuts.For(VirtualKey.Right, Modifier.Command, mac: false, Library));
    }

    /// <summary>A second modifier is another shortcut: Shift extends a selection, and AltGr on a Windows
    /// keyboard is Ctrl and Alt together.</summary>
    [Theory]
    [InlineData(Modifier.Control | Modifier.Shift)]
    [InlineData(Modifier.Control | Modifier.Option)]
    [InlineData(Modifier.None)]
    public void Only_the_one_modifier(object held)
        => Assert.Equal(Shortcut.None, Shortcuts.For(VirtualKey.F, (Modifier)held, mac: false, Library));

    /// <summary>A text field keeps the arrows, which move its caret by a word or to the end of the line,
    /// and what is built on one keeps them too.</summary>
    [Theory]
    [InlineData(typeof(TextBox))]
    [InlineData(typeof(PasswordBox))]
    [InlineData(typeof(RichEditBox))]
    [InlineData(typeof(AutoSuggestBox))]
    [InlineData(typeof(NumberField))]
    public void A_text_field_keeps_the_arrows(Type field)
    {
        Type[] route = [field, .. Library];
        foreach (var key in new[] { VirtualKey.Left, VirtualKey.Right, VirtualKey.Up, VirtualKey.Down })
        {
            Assert.Equal(Shortcut.None, Shortcuts.For(key, Modifier.Command, mac: true, route));
            Assert.Equal(Shortcut.None, Shortcuts.For(key, Modifier.Control, mac: false, route));
        }
    }

    /// <summary>Not F either: a search asked for from inside another field still goes to the
    /// search.</summary>
    [Fact]
    public void A_text_field_does_not_keep_F()
        => Assert.Equal(Shortcut.Search, Shortcuts.For(VirtualKey.F, Modifier.Command, mac: true, [typeof(TextBox), .. Library]));

    /// <summary>The library table moves its focused row with Ctrl and Up or Down, so on Windows those are
    /// the table's. Left and Right it does not answer, and on a Mac the shortcut is Cmd, which the table
    /// does not read.</summary>
    [Fact]
    public void The_table_keeps_ctrl_up_and_down_on_windows()
    {
        Type[] route = [typeof(RpmGrid), .. Library];

        Assert.Equal(Shortcut.None, Shortcuts.For(VirtualKey.Up, Modifier.Control, mac: false, route));
        Assert.Equal(Shortcut.None, Shortcuts.For(VirtualKey.Down, Modifier.Control, mac: false, route));
        Assert.Equal(Shortcut.Next, Shortcuts.For(VirtualKey.Right, Modifier.Control, mac: false, route));

        Assert.Equal(Shortcut.VolumeUp, Shortcuts.For(VirtualKey.Up, Modifier.Command, mac: true, route));
        Assert.Equal(Shortcut.VolumeDown, Shortcuts.For(VirtualKey.Down, Modifier.Command, mac: true, route));
    }

    /// <summary>Nothing focused is the plain case.</summary>
    [Fact]
    public void Nothing_focused_takes_the_shortcut()
        => Assert.Equal(Shortcut.Previous, Shortcuts.For(VirtualKey.Left, Modifier.Command, mac: true, []));

    /// <summary>Held, the volume steps on; a track change or the search happens once per press.</summary>
    [Fact]
    public void Only_the_volume_acts_on_a_repeat()
    {
        Assert.True(Shortcuts.ActsOnRepeat(Shortcut.VolumeUp));
        Assert.True(Shortcuts.ActsOnRepeat(Shortcut.VolumeDown));
        Assert.False(Shortcuts.ActsOnRepeat(Shortcut.Next));
        Assert.False(Shortcuts.ActsOnRepeat(Shortcut.Previous));
        Assert.False(Shortcuts.ActsOnRepeat(Shortcut.Search));
    }

    /// <summary>The modifiers are read only for these five keys, so typing in a field asks
    /// nothing.</summary>
    [Fact]
    public void Only_five_keys_are_asked_about()
    {
        foreach (var key in new[] { VirtualKey.Left, VirtualKey.Right, VirtualKey.Up, VirtualKey.Down, VirtualKey.F })
            Assert.True(Shortcuts.IsShortcutKey(key));

        Assert.False(Shortcuts.IsShortcutKey(VirtualKey.A));
        Assert.False(Shortcuts.IsShortcutKey(VirtualKey.Space));
        Assert.False(Shortcuts.IsShortcutKey(VirtualKey.Enter));
    }

    /// <summary>Stands in for a control built on a text field, such as a number box's inner box.</summary>
    private sealed partial class NumberField : TextBox;
}

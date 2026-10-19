using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RPM.Desktop.Presentation.Main;
using RPM.Desktop.Presentation.Pegasus;
using RPM.Desktop.Presentation.Shell;
using RPM.Desktop.Presentation.Viz;
using RPM.Desktop.Presentation.Grids;

namespace RPM.Desktop.Tests;

/// <summary>
/// The space bar pauses the record and starts it again, from anywhere in the window, unless whatever has
/// the keyboard already has an answer for the key.
///
/// <para>These are types and nothing else: <see cref="SpaceBar"/> is asked about the focused control's type
/// and its parents' in order, which is the whole of what it reads, and is why the rule can be held to here
/// with no window to focus anything in.</para>
/// </summary>
public class SpaceBarTests
{
    /// <summary>Nothing focused at all, such as a listener who has clicked nowhere. The plain case:
    /// the key is the player's.</summary>
    [Fact]
    public void Nothing_focused_is_the_players()
        => Assert.True(SpaceBar.Under([]).BelongsToTransport);

    /// <summary>The shell itself, and the panes and grids inside it, have no answer for the key.</summary>
    [Fact]
    public void The_shell_yields()
        => Assert.True(SpaceBar.Under([typeof(Grid), typeof(MainPage), typeof(ShellPage)]).BelongsToTransport);

    /// <summary>The library grid, which has the keyboard and would otherwise answer the key itself. It is
    /// the player's, and the shell takes the key whole so the grid never sees it.
    ///
    /// <para>The claim is made on one type on the route. The grid paints itself (see <see cref="RpmGrid"/>)
    /// and has no element per cell for the focus to be in. The claim is made by TYPE, not by a cell, a
    /// row or the grid.</para></summary>
    [Fact]
    public void The_library_grid_yields()
        => Assert.True(SpaceBar.Under(
            [typeof(RpmGrid), typeof(MainPage), typeof(ShellPage)])
            .BelongsToTransport);

    /// <summary>A field being typed into keeps its space. This is the one that matters most: a search box
    /// that pauses the music instead of separating two words is not a search box.</summary>
    [Theory]
    [InlineData(typeof(TextBox))]
    [InlineData(typeof(PasswordBox))]
    [InlineData(typeof(RichEditBox))]
    [InlineData(typeof(AutoSuggestBox))]
    public void A_field_keeps_the_key(Type field)
        => Assert.False(SpaceBar.Under([field, typeof(Grid), typeof(ShellPage)]).BelongsToTransport);

    /// <summary>A number box types into a TextBox of its own, and the grid's cell editors are TextBoxes
    /// too: the claim is made by what a control is BUILT ON, not by an exact match.</summary>
    [Fact]
    public void What_is_built_on_a_field_keeps_the_key()
        => Assert.False(SpaceBar.Under([typeof(BoxOfOurOwn), typeof(ShellPage)]).BelongsToTransport);

    private sealed partial class BoxOfOurOwn : TextBox;

    /// <summary>Space is a button's press, a check box's tick and a combo box's drop-down. Whoever focused
    /// one meant it.</summary>
    [Theory]
    [InlineData(typeof(Button))]
    [InlineData(typeof(CheckBox))]
    [InlineData(typeof(RadioButton))]
    [InlineData(typeof(ToggleSwitch))]
    [InlineData(typeof(ComboBox))]
    public void A_control_that_presses_keeps_the_key(Type control)
        => Assert.False(SpaceBar.Under([control, typeof(Grid), typeof(ShellPage)]).BelongsToTransport);

    /// <summary>The full-screen views play and pause on the key themselves and bring their chrome back up
    /// with it. They claim it for everything inside them, so the claim has to be read from the PARENTS as
    /// well as from the focused control.</summary>
    [Theory]
    [InlineData(typeof(PegasusPlayer))]
    [InlineData(typeof(VizFullScreen))]
    public void A_full_screen_view_keeps_the_key_for_what_is_inside_it(Type view)
        => Assert.False(SpaceBar.Under([typeof(Grid), view, typeof(ShellPage)]).BelongsToTransport);
}

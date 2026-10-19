using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using RPM.Desktop.Presentation.Pegasus;
using RPM.Desktop.Presentation.Viz;

namespace RPM.Desktop.Presentation.Shell;

/// <summary>
/// Who the space bar belongs to, decided once for the whole window.
///
/// <para>Space is the transport's key: it pauses the music and starts it again. This is true everywhere
/// in the window (the library, the wall, the artist's page, the queue, the skin), EXCEPT where the key
/// already means something to the control that has the keyboard. A text field types a space between two
/// words. A button is pressed by it. The full-screen views answer it themselves and show their chrome
/// again with it. Those controls keep the key. In all other cases, including when nothing is focused, the
/// transport takes it.</para>
///
/// <para>The question is asked of the ROUTE (the focused control and every parent above it up to the
/// shell), not of the focused control alone, because a claim can come from either place. A text box
/// claims the key for itself. The full-screen player claims it for everything inside it, including the
/// search field in its drawer and the buttons on its chrome. There is one list of claimants, read the same
/// way at every step of the route, so only one place changes when a new surface wants the key.</para>
///
/// <para>The result depends only on types. This lets <c>SpaceBarTests</c> test the rule without a running
/// window, and it is why the shell's handler passes a route of types and not the elements
/// themselves.</para>
/// </summary>
internal readonly struct SpaceBar
{
    /// <summary>The kinds of control that answer the space bar themselves. These are base types, matched
    /// by assignment, so controls built on them are covered too: the cell editors and the inner box of a
    /// NumberBox are TextBoxes, and every button, check box, radio and toggle is a ButtonBase.</summary>
    private static readonly Type[] Claimants =
    [
        typeof(TextBox),            // a space between two words
        typeof(PasswordBox),
        typeof(RichEditBox),
        typeof(AutoSuggestBox),
        typeof(ButtonBase),         // space is the press
        typeof(ToggleSwitch),
        typeof(ComboBox),           // space opens the list
        typeof(PegasusPlayer),      // the full-screen views play and pause on it themselves, and show
        typeof(VizFullScreen),      // their own chrome with it (see their Root_KeyDown)
    ];

    private SpaceBar(bool belongsToTransport) => BelongsToTransport = belongsToTransport;

    /// <summary>True when the key belongs to the player. This is the case unless a control on the route
    /// claimed it.</summary>
    public bool BelongsToTransport { get; }

    /// <summary>
    /// The owner of a press, given the focused control's type and its parents' types in order. An empty
    /// route (nothing focused) belongs to the transport. This is the plain case of a listener who has
    /// not clicked anywhere and wants to stop the music.
    /// </summary>
    public static SpaceBar Under(IEnumerable<Type> route) => new(!route.Any(Claims));

    private static bool Claims(Type element) => Claimants.Any(c => c.IsAssignableFrom(element));
}

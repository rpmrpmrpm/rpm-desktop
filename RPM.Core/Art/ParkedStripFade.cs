namespace RPM.Core.Art;

/// <summary>What the parked strip should do about a decision that has just been taken.</summary>
public enum ParkedFadeAction
{
    /// <summary>Nothing to do: the strip is already going where it has just been asked to go.</summary>
    Nothing,

    /// <summary>Fade the lines in. They were still on screen (fading out), so their geometry stands.</summary>
    FadeIn,

    /// <summary>Fade the lines in, and lay the strip out first: it is coming back from fully hidden,
    /// so it is currently using the open view's shape.</summary>
    FadeInAndLayout,

    /// <summary>Fade the lines out. The strip keeps its parked shape until they have actually gone.</summary>
    FadeOut,
}

/// <summary>
/// Whether the mini player's lines are up, and what a change of mind about that means.
///
/// <para>This type keeps two flags, not one, and that is its whole point. What is ON SCREEN and what is
/// WANTED are the same thing except for the length of a fade, and a fade is exactly when awkward
/// calls arrive. A track change clears the now-playing text on the way out of one song and sets it
/// again on the way into the next, both inside the fade's own 180 ms. If the second call were checked
/// against the on-screen flag, it would read "already showing" (the lines are still there, fading)
/// and do nothing. The fade would then run on to zero with nothing left to bring it back, and the
/// parked player would lose its words and keep its scrub line.</para>
///
/// <para>The fade-out's completion is guarded the same way. It belongs to a departure, and if the
/// strip has since been asked back, that departure never happened.</para>
/// </summary>
public sealed class ParkedStripFade
{
    private bool _wanted;
    private bool _onScreen;

    /// <summary>True while the lines are on screen, including while they are fading away.</summary>
    public bool IsOnScreen => _onScreen;

    /// <summary>What the last decision asked for.</summary>
    public bool IsWanted => _wanted;

    /// <summary>Asks for the lines up or down, and says what that means for the animation.</summary>
    public ParkedFadeAction Want(bool show)
    {
        if (show == _wanted)
            return ParkedFadeAction.Nothing;

        _wanted = show;
        if (!show)
            return ParkedFadeAction.FadeOut;

        var fresh = !_onScreen;
        _onScreen = true;
        return fresh ? ParkedFadeAction.FadeInAndLayout : ParkedFadeAction.FadeIn;
    }

    /// <summary>The fade-out finished. True when the lines are fully gone and the strip may go back
    /// to the open view's shape; false when they were asked back mid-fade and this completion is stale.</summary>
    public bool NoteFadedOut()
    {
        if (_wanted)
            return false;
        _onScreen = false;
        return true;
    }
}

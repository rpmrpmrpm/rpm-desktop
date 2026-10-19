namespace RPM.Core.Ripping;

/// <summary>
/// Decides when a disc that turned up on its own is worth asking about. One question per disc: the
/// answer is asked for the moment a disc appears, and never again until that disc leaves the drive.
///
/// <para>A drive is polled, not subscribed to. There is no cross-platform "disc inserted" event, so
/// the arrival of a disc is a change between two polls, not a notification from the OS. That edge is
/// the whole of this class. It lives here, not in the watcher, because it holds the rules: a disc
/// already being ripped is never offered, a disc the user said no to stays refused, and taking a
/// disc out and putting it back is a new disc with a fresh question.</para>
/// </summary>
public sealed class DiscOfferGate
{
    /// <summary>True once this disc's question has been settled: it was asked, or it was skipped
    /// because the drive was already busy. Cleared when the drive goes empty.</summary>
    private bool _answered;

    /// <summary>
    /// Feeds one poll in and says whether to put the question up now.
    /// </summary>
    /// <param name="present">Whether a disc is in the drive at this poll.</param>
    /// <param name="busy">Whether the app is already doing something with a disc: a rip running, or
    /// an offer still on screen. A disc being ripped is one the user has already answered for.</param>
    public bool Poll(bool present, bool busy)
    {
        // An empty drive resets everything: the next disc is a different disc, even when it is
        // physically the same one going back in.
        if (!present)
        {
            _answered = false;
            return false;
        }

        // The drive is in use, so there is nothing to ask. The disc counts as answered, so a rip that
        // finishes with the disc still in does not come back as a fresh offer.
        if (busy)
        {
            _answered = true;
            return false;
        }

        if (_answered)
            return false;

        _answered = true;
        return true;
    }
}

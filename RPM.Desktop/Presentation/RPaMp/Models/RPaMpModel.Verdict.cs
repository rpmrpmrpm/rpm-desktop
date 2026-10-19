using Microsoft.Extensions.DependencyInjection;
using RPM.Core.Library;
using RPM.Core.Playback;

namespace RPM.Desktop.Presentation.RPaMp.Models;

/// <summary>
/// The two keys at the transport that say what the listener thinks of what is playing.
///
/// <para><b>These are not the heart.</b> A favourite is a statement about the song and is stored in
/// its own column. A verdict is a statement about the pick: the generated list offered a song, and the
/// listener says that it belonged or that it did not. Pressing like here does not favourite the song.
/// See <see cref="TrackVerdict"/>.</para>
///
/// <para><b>Read from the library when it is asked for, never remembered.</b> The heart at the
/// television follows the same rule. A verdict can be written from here, from a track line, from the
/// album view and from the full-screen player. A value read when the consumer needs it is right
/// whoever wrote it. Nothing here caches, so nothing here can be stale.</para>
///
/// <para><b>Shown only while a generated list is playing.</b> A refusal takes a song out of the lists
/// that the library writes itself, and a like keeps it in them. Neither has any effect on an album that
/// the listener chose. A key that changes nothing invites an opinion and silently drops it, which is
/// worse than no key.</para>
///
/// <para><see cref="QueueSource"/> records this. A file with no row in any collection (a radio stream,
/// or a file dragged in from the desktop) has nowhere to store a verdict, so the keys are also hidden
/// for it.</para>
/// </summary>
public sealed partial class RPaMpModel
{
    private static Services.Library.LibraryService Shelf =>
        App.Services.GetRequiredService<Services.Library.LibraryService>();

    /// <summary>Whether there is anywhere to write an opinion about what is playing. This separates a
    /// key that is off from a key that cannot be pressed. See <c>LibraryService.HoldsTrack</c>, which
    /// answers this question.</summary>
    public bool CanJudge
    {
        get
        {
            try
            {
                return _playback.PlayingPath is { } path && Shelf.HoldsTrack(path);
            }
            catch (InvalidOperationException)
            {
                // There is no host in tests and just before or after a launch. In that case there is
                // nowhere to write an opinion.
                return false;
            }
        }
    }

    /// <summary>
    /// What the listener has already said about what is playing, IN THE LIST THAT IS PLAYING.
    ///
    /// <para>A verdict is about a list, not about a song. The same song can be lit under Chill and dim
    /// under Dancefloor. The keys are shown only while a generated list is playing
    /// (<see cref="IsVerdictShown"/>), so they always have a list to speak for.</para>
    /// </summary>
    public TrackVerdict Verdict
    {
        get
        {
            if (Source.Generated is not { } id || _playback.PlayingPath is not { } path)
                return TrackVerdict.None;

            try
            {
                return Shelf.TrackVerdictOf(path, id.Kind);
            }
            catch (InvalidOperationException)
            {
                return TrackVerdict.None;
            }
        }
    }

    /// <summary>
    /// Where the songs in the queue came from.
    ///
    /// <para><b>Cleared by every method that fills the queue, and set only by one that says so.</b>
    /// Anything that puts music on the queue clears this first, so the default is "not a generated
    /// list" and the keys are hidden. A new fill method that forgets this also hides the keys, which is
    /// the harmless result. Only <see cref="NoteGenerated"/> sets it, and only the two places that play
    /// one of the library's own lists call it.</para>
    ///
    /// <para><b>Why it is not a required argument of the fill methods.</b> There are five of them and
    /// many call sites, almost all of which would pass the same value, and the value only decides
    /// whether a button is drawn. The safe default avoids that work. If a verdict is ever used for
    /// something more important than a button, change this.</para>
    /// </summary>
    public QueueSource Source { get; private set; } = QueueSource.Elsewhere;

    /// <summary>The queue is being filled from somewhere else, so the stored source is no longer
    /// true. Every method that puts music in the queue calls this first.</summary>
    internal void ForgetSource()
    {
        if (Source.IsGenerated)
        {
            Source = QueueSource.Elsewhere;
            RaiseVerdict();
        }
    }

    /// <summary>
    /// Says the queue now holds one of the lists the library writes itself.
    ///
    /// <para><b>Call it after the fill, never before.</b> The fill clears this. Both calls reach the model
    /// through the same dispatcher queue in the order they were made, so a caller that plays the tracks
    /// and then calls this gets the right result. In the other order, the fill clears the source and the
    /// keys never appear.</para>
    /// </summary>
    public void NoteGenerated(SmartPlaylistId id)
    {
        Source = QueueSource.Generation(id);
        RaiseVerdict();
    }

    /// <summary>Whether the keys are shown at all: a generated list is playing and there is somewhere
    /// to write the answer. It is bound as a Visibility through the shared converter, like every other
    /// key on the bar that can be hidden.</summary>
    public bool IsVerdictShown => Source.IsGenerated && CanJudge;

    /// <summary>Full when the listener has given this verdict, dim when they have not. Opacity is used
    /// instead of a colour: the transport is a row of flat glyphs that share one colour, and a red key
    /// among them would look like a warning, not an opinion.</summary>
    public double LikeOpacity => Verdict == TrackVerdict.Liked ? 1.0 : DimKey;

    public double DislikeOpacity => Verdict == TrackVerdict.Disliked ? 1.0 : DimKey;

    /// <summary>The opacity of a key that is not pressed. It is the same number that the transport's own
    /// disabled keys use, so a verdict that nobody gave looks like part of the bar and not like
    /// something switched off.</summary>
    private const double DimKey = 0.45;

    /// <summary>
    /// Says the song belonged where the list put it, or takes it back.
    ///
    /// <para>Pressing a key that is already lit clears it. It must be the same key that clears the
    /// verdict, because a listener who pressed the wrong key reaches for that key again.</para>
    /// </summary>
    public void Like() => Say(TrackVerdict.Liked);

    /// <summary>
    /// Says the song did not belong, and acts on it at once.
    ///
    /// <para><b>A refusal takes effect immediately.</b> If the app only stored the refusal and let the
    /// song play on, the listener would stop pressing the key. So the song leaves the queue and the
    /// next song starts.</para>
    ///
    /// <para>Taking a refusal back does not put the song back in the queue. It is already gone and
    /// another song is playing. The second press undoes the lasting part: generated lists can offer
    /// the song again (<c>LibraryDatabase.NotTurnedDown</c>).</para>
    /// </summary>
    public void TurnDown()
    {
        var refused = Verdict != TrackVerdict.Disliked;

        Say(TrackVerdict.Disliked);

        if (refused)
            DropAndMoveOn();
    }

    /// <summary>
    /// Takes the playing song out of the queue and starts whatever has moved up into its place.
    ///
    /// <para>This does not call <see cref="RemoveAt"/>. That method stops playback when it removes the
    /// playing track, which is right when a person edits the queue by hand, but here another song must
    /// play.</para>
    ///
    /// <para><b>A DJ mix is left alone.</b> While the mix runs, the next track is already cued,
    /// tempo-matched and part way into a blend. Removing the current track would cut the mix instead
    /// of skipping a song. The refusal is still stored and still keeps the song out of every list. Only
    /// the immediate skip waits.</para>
    /// </summary>
    private void DropAndMoveOn()
    {
        if (IsDjMixing || _currentIndex < 0 || _currentIndex >= Playlist.Count)
            return;

        var at = _currentIndex;
        Playlist.RemoveAt(at);

        // The track prepared to follow may be the one just removed, or may now be at a different
        // index. RemoveAt clears it for the same reason.
        _playback.ClearChain();

        Renumber();
        OnChanged(nameof(HasTracks));
        OnChanged(nameof(TotalTimeText));

        if (Playlist.Count == 0)
        {
            _currentIndex = -1;
            StopPlayback();
            RaiseNowPlaying();
            return;
        }

        // Play the track now at the index of the refused song, which is the one that was next. After
        // the end of the queue it wraps to the start, as stepping forward does.
        PlayIndex(at < Playlist.Count ? at : 0, "the playing song was turned down");
    }

    /// <summary>
    /// Writes one verdict and tells the deck to redraw.
    ///
    /// <para>Pressing the key that is already lit means <see cref="TrackVerdict.None"/>. The same key
    /// gives an opinion and takes it back, so no key on the bar needs three states.</para>
    ///
    /// <para>If the library returns false, there was nowhere to store the verdict. The keys keep what
    /// they showed.</para>
    /// </summary>
    private void Say(TrackVerdict verdict)
    {
        // The list is part of the verdict. With no generated list playing, the keys are not on the
        // bar, so this point is not reached. A verdict with no list to attach to would mean nothing.
        if (Source.Generated is not { } id || _playback.PlayingPath is not { } path)
            return;

        var next = Verdict == verdict ? TrackVerdict.None : verdict;

        try
        {
            if (!Shelf.SetTrackVerdict(path, id.Kind, next))
                return;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        RaiseVerdict();
    }

    /// <summary>Makes the keys read their values again. Call it when a verdict is written here and
    /// whenever the playing track changes, because a new song has its own verdict and the keys must not
    /// show the last one's.</summary>
    internal void RaiseVerdict()
    {
        OnChanged(nameof(Verdict));
        OnChanged(nameof(CanJudge));
        OnChanged(nameof(IsVerdictShown));
        OnChanged(nameof(LikeOpacity));
        OnChanged(nameof(DislikeOpacity));
    }
}

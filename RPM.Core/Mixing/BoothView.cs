namespace RPM.Core.Mixing;

/// <summary>
/// What the booth is doing, as one answer.
///
/// <para>The booth has about a dozen fields. Each one says something about the booth: a plan held ready, a
/// fade counting frames, a platter stopped, a hand on the decks, a decoder still opening. Each control on
/// the workbench would combine its own set of these. That is how a page can offer an edit that the set then
/// drops. Each question is answered correctly, but together they make a screen that shows a booth nobody is
/// in.</para>
///
/// <para>So there is one question with one answer. Everything the workbench enables, disables or says is
/// read from a <see cref="BoothView"/> and from nothing else.</para>
/// </summary>
public enum BoothState
{
    /// <summary>No set, or a set whose first record has not reached a deck yet.</summary>
    Nothing,

    /// <summary>The set is finished.</summary>
    Over,

    /// <summary>
    /// A record is playing, and no changeover will be made from it. See <see cref="BoothView.Why"/> for the
    /// reason.
    ///
    /// <para>The decks keep playing either way. So a workbench that only knew "a record is playing" could
    /// not tell a DJ who is working towards the next changeover from one who has given up on it.</para>
    /// </summary>
    DeckPlaying,

    /// <summary>The DJ has the decks, and a changeover out of the record playing will happen. It has not
    /// started yet, so it can still be changed.</summary>
    Mixing,

    /// <summary>The changeover is running. From here it is frames on the audio thread, and nothing about it
    /// may be moved. See <see cref="Dj.Replan"/>, which refuses.</summary>
    HandingOver,
}

/// <summary>Why no changeover will be made from the record playing. Only meaningful in <see
/// cref="BoothState.DeckPlaying"/>.</summary>
public enum NoMixBecause
{
    /// <summary>A changeover is coming. This is not that state.</summary>
    None,

    /// <summary>The decks are in a person's hands. The set does nothing while they are. See <see
    /// cref="Dj.TakeTheDecks"/>.</summary>
    HandsOnTheDecks,

    /// <summary>The record coming in is being played by hand, so the set is waiting.</summary>
    Auditioning,

    /// <summary>The needle was moved into the last part of the record, past the point where a changeover
    /// can still start. The record plays out, and the next one follows it. See <see cref="Dj.SeekTo"/>.
    /// Scrubbing back out of that part turns the changeover back on.</summary>
    ScrubbedPastTheMixPoint,

    /// <summary>Nothing is left in the crate. This is the last record of the set.</summary>
    NothingLeftInTheCrate,

    /// <summary>Both records are being put back, so that the same changeover can be tried again. See <see
    /// cref="Dj.RehearseChangeover"/>.</summary>
    PuttingThePairBack,

    /// <summary>
    /// The needle is past the point where the set was going to hand over, and nothing is cued. The
    /// changeover that was shown will not happen. The set picks another one from where the record is now.
    /// Until it has, no mix is coming.
    /// </summary>
    TheMomentHasPassed,
}

/// <summary>
/// One reading of the booth, taken in one pass. Every permission the workbench needs is already worked out.
///
/// <para><b>Why the permissions are here and not on the page.</b> A control that can be pressed while the
/// set will ignore the press is worse than a control that is shut. The request is made, the picture moves,
/// the journal records an opinion, and the set plays the changeover it was always going to play. Each of
/// those refusals is a rule inside <see cref="Dj"/>: a fade that has started, a decoder still opening, a
/// hand on the decks. So the answer to "may this be pressed?" is worked out where those rules are, and it
/// is handed out as a fact.</para>
///
/// <para>This is a snapshot, not a live object. The audio thread can change the booth at any moment. The
/// point of the snapshot is one consistent view of the interface, instead of eleven questions asked at
/// eleven different instants.</para>
/// </summary>
public sealed record BoothView
{
    /// <summary>The one answer. Everything on screen is named by this.</summary>
    public required BoothState State { get; init; }

    /// <summary>In <see cref="BoothState.DeckPlaying"/>, the reason it is that state.</summary>
    public NoMixBecause Why { get; init; }

    /// <summary>The engine is stopped. This is not the same as the decks being still. A paused set is two
    /// stopped platters with the engine still turning, so the booth can still be asked for things.
    /// </summary>
    public required bool Paused { get; init; }

    /// <summary>The decks are in a person's hands, not the set's.</summary>
    public required bool HandsOnTheDecks { get; init; }

    /// <summary>Whether each deck's needle is actually moving. See <see cref="Dj.IsDeckTurning"/>. This is
    /// what "deck 1 is playing" means. It is not the same as the deck being loaded, or even
    /// started.</summary>
    public required bool LiveTurning { get; init; }

    /// <inheritdoc cref="LiveTurning"/>
    public required bool CuedTurning { get; init; }

    /// <summary>A record is on the second platter with a decoder open on it. This is more than a choice
    /// showing in the second panel.</summary>
    public required bool CuedOnAPlatter { get; init; }

    /// <summary>
    /// A decoder is opening on the free deck. While it does, nothing that would take a record off that deck
    /// is allowed. <see cref="Dj.Replan"/>, <see cref="Dj.Reject"/> and <see cref="Dj.TakeTheDecks"/> all
    /// refuse. This lasts a few hundred milliseconds, and it can land in the exact stretch of time in which
    /// a workbench is being used.
    /// </summary>
    public required bool CueThreadHasADeck { get; init; }

    /// <summary>The record playing is being eased onto the tempo that the changeover will run at.</summary>
    public required bool MatchingIn { get; init; }

    /// <summary>
    /// A changeover that has a shape to argue with will be made from the record playing.
    ///
    /// <para>This is the fact underneath <see cref="BoothState.Mixing"/>, but it is not the same question.
    /// While the decks are in a person's hands, the set stops watching for the outro. When the decks are
    /// given back, the mix is on again. So the strip stays live through a pause, which is the state the
    /// workbench is meant to be used in. This becomes false when the mix itself is off: the needle is past
    /// the mix point, nothing is left in the crate, or the pair is being put back.</para>
    /// </summary>
    public required bool MixIsComing { get; init; }

    /// <summary>
    /// Where on the record playing the DJ will start looking to hand over. This is the DJ's own answer,
    /// under the DJ's own overrides, in the record's own seconds. It is zero where no mix is coming.
    ///
    /// <para>This is reported because no other number on the workbench can be checked against it. Marks
    /// drawn on a record are a plan that someone worked out. This number is where the set will actually
    /// act. If the two disagree, this number is what shows it. It stays invisible unless one of them is
    /// written down as a number.</para>
    /// </summary>
    public required double ArmsAtSeconds { get; init; }

    /// <summary>Where the needle on the record playing is, in that record's own seconds. It is carried here
    /// so that "is the mark behind us" is checked against the same instant as everything else.</summary>
    public required double LiveAtSeconds { get; init; }

    /// <summary>
    /// The record playing has already gone past the point where the DJ was going to hand over.
    ///
    /// <para>This is not a state of its own. The set is still mixing and will still choose something. But a
    /// mark drawn behind the needle means exactly this. A workbench that draws that mark without saying so
    /// claims a changeover that has already been missed.</para>
    /// </summary>
    public bool NeedleIsPastTheArmPoint => Why is NoMixBecause.TheMomentHasPassed;

    // What may be touched

    /// <summary>
    /// The changeover may be changed: its marks moved, its style picked, its length set, its arc drawn.
    /// This is true only when an edit would reach the set.
    ///
    /// <para>It is true while the decks are in a person's hands. That is the state the workbench is meant
    /// to be used in: the set stopped, the marks moved, then played to hear what the move did. The refusals
    /// that matter are the ones that are not about who has the decks. A changeover that has started is
    /// frames on the audio thread. A deck with a decoder opening on it belongs to the cue thread until that
    /// decoder is ready. A mix that will not be made has nothing to argue about. <see cref="Dj.Replan"/>
    /// refuses the first two. A control that stayed live over either one would accept edits and then drop
    /// them.</para>
    /// </summary>
    public bool CanEditTheMix =>
        MixIsComing && State is not BoothState.HandingOver && !CueThreadHasADeck;

    /// <summary>
    /// A record's own reading may be corrected: its grid moved, its downbeat named, its intro and outro
    /// marks moved. This permission is wider than <see cref="CanEditTheMix"/> on purpose. A grid is worth
    /// fixing whether or not a changeover will be made from this record. The reading is what gets
    /// published, not the changeover.
    /// </summary>
    public bool CanEditTheReading =>
        (State is BoothState.Mixing or BoothState.DeckPlaying) && !CueThreadHasADeck;

    /// <summary>Each deck answers its own transport: play this record, stop it, or put its needle on its
    /// mark. This is only allowed while the decks are in a person's hands. While the set has them, a press
    /// would start a changeover that nobody asked for.</summary>
    public bool CanDriveTheDecks =>
        HandsOnTheDecks && State is not (BoothState.Nothing or BoothState.Over or BoothState.HandingOver);

    /// <summary>The needle on the record playing may be moved. This is refused for the whole changeover,
    /// which is the one time the record playing is not the only thing playing.</summary>
    public bool CanScrubTheLiveDeck =>
        State is BoothState.Mixing or BoothState.DeckPlaying;

    /// <summary>The needle on the record coming in may be moved. This is only allowed while a person has
    /// that deck and there is a record on it. Otherwise its needle belongs to the plan that cued
    /// it.</summary>
    public bool CanScrubTheCuedDeck =>
        CuedOnAPlatter && (HandsOnTheDecks || Why is NoMixBecause.Auditioning) &&
        State is not BoothState.HandingOver;

    /// <summary>The next key. Refused during a changeover, which is already moving to the next
    /// record.</summary>
    public bool CanSkip => State is BoothState.Mixing or BoothState.DeckPlaying;

    /// <summary>The record chosen to come next may be turned down, whether or not it has reached a deck.
    /// Once the changeover has started, it is no longer a choice.</summary>
    public bool CanRejectTheNextRecord =>
        (State is BoothState.Mixing or BoothState.DeckPlaying) && !CueThreadHasADeck;

    /// <summary>The set's own transport may be pressed: the DJ takes the decks or gives them up. This is
    /// refused for the length of a changeover. A fade is choreography across both decks, so nobody may
    /// interrupt it.</summary>
    public bool CanTakeOrGiveTheDecks =>
        State is not (BoothState.Nothing or BoothState.Over or BoothState.HandingOver);

    /// <summary>
    /// What the state is, in one line, for the workbench heading. It says which state is happening and,
    /// where the reason is not obvious, why.
    /// </summary>
    public string Headline => State switch
    {
        BoothState.Nothing => "No set running",
        BoothState.Over => "Set finished",
        BoothState.HandingOver => "Changeover running (nothing adjustable)",
        BoothState.Mixing => Paused
            ? "Mixing (paused)"
            : MatchingIn ? "Mixing (riding the tempo across)" : "Mixing",
        _ => Why switch
        {
            NoMixBecause.HandsOnTheDecks => Playing("Decks yours"),
            NoMixBecause.Auditioning => "Playing deck 2 (the set is holding)",
            NoMixBecause.ScrubbedPastTheMixPoint =>
                "Playing out, no mix (the needle is past the mix point)",
            NoMixBecause.NothingLeftInTheCrate => "Playing out, no mix (last record of the set)",
            NoMixBecause.TheMomentHasPassed => "Not mixing (the needle is past the hand-over point)",
            NoMixBecause.PuttingThePairBack => "Putting both records back",
            _ => Playing("Playing"),
        },
    };

    private string Playing(string lead) =>
        (LiveTurning, CuedTurning) switch
        {
            (true, true) => $"{lead}: deck 1 and deck 2 playing",
            (true, false) => $"{lead}: deck 1 playing",
            (false, true) => $"{lead}: deck 2 playing",
            _ => Paused ? $"{lead}: both decks stopped" : $"{lead}: nothing playing",
        };

    /// <summary>The reading of an empty booth: no set, nothing on a deck, nothing to press.</summary>
    public static BoothView Empty { get; } = new()
    {
        State = BoothState.Nothing,
        Paused = false,
        HandsOnTheDecks = false,
        LiveTurning = false,
        CuedTurning = false,
        CuedOnAPlatter = false,
        CueThreadHasADeck = false,
        MatchingIn = false,
        MixIsComing = false,
        ArmsAtSeconds = 0,
        LiveAtSeconds = 0,
    };
}

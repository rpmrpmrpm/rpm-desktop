using RPM.Core.Metadata;

namespace RPM.Core.Library;

/// <summary>Which end of its own kind of music a track sits at, or neither.
///
/// <para><see cref="None"/> is the ordinary answer, and it is deliberately not a name. Most of a collection
/// is in the middle. A middle that was given a name, such as "Moderate" or "Balanced", would be a playlist
/// of everything under a word that says nothing. A track at neither end is in no list, rather than being
/// labelled.</para>
/// </summary>
public enum MoodBand
{
    /// <summary>Neither end. Most of the shelf, and not a label.</summary>
    None,

    /// <summary>The quiet, still end of this kind of music.</summary>
    Calm,

    /// <summary>The loud, busy end of it.</summary>
    Energetic,
}

/// <summary>What a track is called, once the shelf it stands on has been taken into account.
/// </summary>
/// <param name="Band">Which end it sits at, or <see cref="MoodBand.None"/>.</param>
/// <param name="Danceable">Busy and even: a lot of events over a level that barely moves. It sits beside
/// the band, not as a third value of it, because it is a different question. A record can be at the
/// energetic end and not danceable (a live rock band that swells and drops). It can also be danceable
/// without being at either end, which is most of what a house record is among other house records.</param>
/// <param name="Genre">The record's genre as the app files it, or null where it has none the app
/// recognises. This is the OTHER half of the label. The same band means a different thing on a jazz record
/// than on a metal one, and the word the listener reads should say which.</param>
/// <param name="Confidence">0..1. How much to believe the band. Nought where there is no band to
/// believe.</param>
/// <param name="Drive">0..1. How far past BOTH danceable conditions the track sits, measured by the weaker
/// of them. This is to <see cref="Danceable"/> what <see cref="Confidence"/> is to a band. It is nought
/// where the track is not danceable. A danceable list needs an order, and band confidence cannot give one.
/// Most danceable tracks are in no band at all, so ranking them by confidence would sort the whole list by
/// nought.</param>
/// <param name="Focused">Sparse and even: few events over a level that barely moves. This is the OTHER half
/// of the evenness axis. <see cref="Danceable"/> is the first half. Both want a level that holds still, and
/// what separates them is whether anything is happening over it. A record cannot be both. This is
/// arithmetic, not a rule imposed here: one wants busy and the other wants sparse.
/// <para>Focused is not a quieter Calm. Calm asks where a record sits on the arousal axis, and this does
/// not ask that at all. A steady loop at high energy is nothing like calm, and it is exactly what somebody
/// working wants. Measured on a real collection, 84% of what this names is in no other mood list. Danceable
/// has about the same share.</para></param>
/// <param name="Stillness">0..1. How far inside BOTH focus conditions the track sits, measured by the
/// weaker of them. This is to <see cref="Focused"/> what <see cref="Drive"/> is to <see cref="Danceable"/>.
/// It is nought where the track is not focused, for the same reason: the list needs an order, and no band
/// can give one.</param>
public readonly record struct MoodLabel(
    MoodBand Band, bool Danceable, string? Genre, double Confidence, double Drive,
    bool Focused, double Stillness)
{
    /// <summary>Nothing to say about this track.</summary>
    public static readonly MoodLabel Nothing = new(MoodBand.None, false, null, 0, 0, false, 0);

    /// <summary>Whether this label is worth showing at all. It names an end, or it says danceable or
    /// focused.</summary>
    public bool Named => Band != MoodBand.None || Danceable || Focused;
}

/// <summary>The recipe. It takes raw arousal measurements and gives one band per track. Each track is cut
/// against the listener's own collection, and against its own genre within that collection.
///
/// <para><b>Cut within the genre. This is the core of the design.</b> A percentile over the whole shelf
/// sorts a mixed collection along the shelf it already has. On a library that is four fifths techno and one
/// fifth classical, every classical record lands calm and every techno record lands energetic. The listener
/// is then told only what the folder names already said. Cut within the genre, and "calm rock" means calm
/// FOR ROCK, which is what somebody means when they say it. The same brightness can be a bright chamber
/// recording or a dull metal record. Only a distribution for each genre can tell which of those it is
/// looking at.</para>
///
/// <para><b>Genre may shape a reading, and may never replace one.</b> This follows the rule of
/// <see cref="Analysis.TempoOctave"/>, which already conditions a measurement on genre in this app. A wrong
/// genre costs a label that should not have appeared. It never costs a measurement. Nothing here is written
/// back to the track rows. The three columns stay the raw numbers the meter produced. This is arithmetic
/// over them, and it runs fresh whenever it is asked.</para>
///
/// <para><b>Pure and database-free</b>, like <see cref="GenreWall"/> and <see cref="HomeShelf"/>. The
/// caller passes in what was measured and what each folder is filed under. This returns the bands. A
/// question asked of a collection is something a test can hold still.</para>
/// </summary>
public sealed class MoodBands
{
    /// <summary>The share of a genre's own tracks at each end.
    ///
    /// <para><b>15 per cent at each end.</b> At a quarter, a great deal of merely middling material
    /// qualifies. A quarter of any shelf is not an END of the axis so much as most of one half of it. The
    /// word has to mean something. "The quietest 15% of your rock" means more than "the quieter
    /// quarter".</para>
    ///
    /// <para><b>What it costs.</b> The bands are percentiles, so this cut is narrower than a quarter would
    /// be. On a real collection of 10,947 measured tracks, the rock band went from about 416 songs to about
    /// 250. The smallest genres that still get a row have thin bands. Two things depend on that depth, and both
    /// were checked. A list shows fifty songs (<see cref="SmartPlaylists.MostSongs"/>), and the generate
    /// button only appears while the pool holds more than fifty. Narrower than this, the small genres
    /// cannot offer a second fifty. Wider, and the word stops meaning anything.</para>
    ///
    /// <para>These cuts are set separately. <see cref="BusyAt"/> and <see cref="SleepAt"/> are not on this
    /// scale. Danceable is not an end of the axis at all. It is two conditions crossing, and it is
    /// deliberately the looser cut (see <see cref="BusyAt"/>). Sleep is an absolute reading over the whole
    /// collection, not a percentile.</para>
    /// </summary>
    public const double CalmAt = 0.15, EnergeticAt = 0.85;

    /// <summary>How busy and how even a track has to be to be called danceable, as ranks within its genre.
    ///
    /// <para>Two conditions, because either one alone is something else. Busy alone is a drum solo or a
    /// thrash record. Even alone is an ambient drone, which is the most evenly levelled thing anybody owns.
    /// Busy AND even together is a lot of events over a floor that does not move. That is what a dance
    /// record is, and it is not what a band recorded live in a room is.</para>
    ///
    /// <para>This cut is looser than the band edges on purpose. It is not an end of the axis. It is a
    /// property that a good third of some genres have, and almost none of others. Cut at a quarter, it
    /// would report that a quarter of every folk shelf is danceable.</para>
    /// </summary>
    public const double BusyAt = 0.65, EvenAt = 0.35;

    /// <summary>How few events a track can carry and still be called focused, as a rank within its genre.
    ///
    /// <para><b>The mirror of <see cref="BusyAt"/> across the same evenness.</b> Focus and Danceable share
    /// <see cref="EvenAt"/> and differ only here. A level that holds still with a great deal happening over
    /// it is a dance record. The same level with little happening over it is music that somebody can work
    /// through. The two cuts do not meet, so no track is ever both. That follows from the numbers, and it
    /// is not enforced anywhere.</para>
    ///
    /// <para><b>Symmetric on purpose. The symmetry is what was checked.</b> At this cut, the focus list is
    /// 13.7% of a real collection of 15,114 measured tracks. Danceable is 9.9% of the same collection.
    /// These are the same order of size. That shows the pair are two readings of one axis, rather than one
    /// broad list and one narrow one. Loosened to 0.45, it becomes 17.3% and starts taking in records that
    /// are merely not busy.</para>
    /// </summary>
    public const double SparseAt = 0.35;

    /// <summary>How loud a track can be mastered and still be one of these two things, in LUFS.
    /// </summary>
    /// <remarks><b>The condition the pair did not have, and the one a listener notices first.</b> Onsets
    /// and dynamics together say "few events over a level that barely moves". A modern dance record
    /// mastered flat satisfies that exactly. A half-time trap or dubstep beat has FEW attacks, because its
    /// energy is sustained bass rather than discrete hits. Its mastering leaves it almost no dynamic range.
    /// Neither cut looked at how loud a track was.
    ///
    /// <para><b>Measured on a real collection.</b> Two sparse, even dance tracks that the
    /// rank tests alone put in Focus.
    /// Snails' "Funk With Me" measured 3.78 onsets a second, against an electronic cohort median of 4.71.
    /// That is rank 0.25, inside Focus' 0.35. Its dynamics rank was 0.10. Diplo's "Boy Oh Boy" measured
    /// 3.68 onsets and 0.15 dynamics. Both are sparse and even by the numbers, and neither is music to work
    /// through. The Thugli remix of that same song measures 5.22 and lands in Dancefloor. That is the whole
    /// fault in one pair of rows. Without this rule, the electronic side of Focus would hold Vangelis at
    /// -27 LUFS beside Skrillex's "Kyoto" at -4.8.</para>
    ///
    /// <para><b>Absolute, where the other three tests are ranks within the genre.</b> Too loud to work
    /// through is a fact about the track, not about its neighbours. A collection that is nothing but EDM
    /// would otherwise still produce a focus list out of its own flattest masters. This is the same
    /// argument that <see cref="SleepAt"/> makes, and it is the precedent for an absolute reading
    /// here.</para>
    ///
    /// <para><b>Choosing -7.</b> On 17,654 measured tracks, -6 takes 89 songs out of Focus and gives
    /// Dancefloor 123. -7 takes 276 (9% of the list) and gives 446. -8 takes 586 (20%) and gives 1,051. -8
    /// starts removing records that are merely well mastered, and -6 barely touches the fault. -7 is about
    /// where a master stops leaving room and starts being made as loud as possible.</para>
    ///
    /// <para><b>A track nobody has measured is left where it was.</b> Null loudness neither refuses a track
    /// from Focus nor admits one to Dancefloor. The loudness pass fills in a shelf over the minutes after
    /// an upgrade. A rule that read null as loud would empty the focus list on every fresh install, and
    /// then refill it.</para>
    /// </remarks>
    public const double LoudAt = -7;

    /// <summary>How still, and how even, a track has to be over the WHOLE collection to be music somebody
    /// could fall asleep to.
    ///
    /// <para><b>Absolute, where every other cut here is within a genre. That is the point of it.</b> Calm
    /// answers "the quiet end of this kind of music". So the calmest techno on the shelf is calm, and it
    /// belongs in that list. Nobody falls asleep to it. Sleep is not a question about a genre at all. It
    /// asks whether a record is quiet, dark and still in absolute terms. That answer has to be taken
    /// against everything the listener owns. Otherwise a collection of nothing but drum and bass would
    /// produce a sleep list out of its own gentlest breakbeats.</para>
    ///
    /// <para>A fifth of the collection. This list is put on to stop paying attention, so it is better short
    /// and right than long and startling.</para>
    /// </summary>
    public const double SleepAt = 0.20;

    /// <summary>How much swing a sleep record is allowed, as a rank over the whole collection.
    ///
    /// <para>The condition that Calm does not have, and the one that matters most here. A nocturne is quiet
    /// and still, and it has a fortissimo in the middle. Measured on onsets and brightness alone, it is
    /// perfect sleep music, and it will wake somebody up. Dropping the most dynamic quarter keeps the
    /// orchestral and jazz records whose loud passages stay near their quiet ones. It turns away the
    /// records that do not. That is the distinction a listener would actually make.</para>
    /// </summary>
    public const double SleepSwingAt = 0.75;

    /// <summary>The shelves that hold rap, under one name.
    ///
    /// <para><b>Named once, because two rules need it.</b> Writing the same list out in two places lets one
    /// copy go stale. Then it is wrong in one place and right in the other, which is worse. A record tagged
    /// "Rap" or "Trap" lands on a shelf of its own. Each of those names must be listed here, so that the
    /// rap rules cover it.</para>
    ///
    /// <para><see cref="Metadata.Genres.All"/> is where these labels live. This set is a reading of it, and
    /// it has to change when that changes.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> Rapping =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "Hip-Hop / Rap", "Rap", "Trap", "Old School Hip Hop",
        };

    /// <summary>The genres that are never sleep music, however still a track from one of them measures.
    ///
    /// <para><b>Measured stillness is not the whole question here.</b> The meter hears level and events. It
    /// does not hear a voice. A quiet rap record is quiet and has few events, and somebody is talking over
    /// the top of it. That is the one thing that sleep music cannot have. No number that this app takes can
    /// tell it apart from an instrumental. The rap genres are named here. Spoken word is the same fault
    /// stated more plainly, so it is listed too.</para>
    ///
    /// <para>This is a veto, never a promotion. A genre on this list keeps a track OUT of one playlist, and
    /// it changes no measurement. That is the rule of <see cref="Analysis.TempoOctave"/> about what a genre
    /// is allowed to do.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> NeverSleep =
        new HashSet<string>(Rapping, StringComparer.Ordinal) { "Spoken Word" };


    /// <summary>The genres that a POOLED mood list leaves out. A pooled list is the whole-shelf list, and
    /// the decade cuts taken from it. It never applies to a genre row.
    ///
    /// <para><b>Rap is left out of the pooled Chill and Focus lists.</b> A rap record in these lists has
    /// the same fault as <see cref="NeverSleep"/>, one step weaker. The meter hears level and events, and
    /// it does not hear a voice. So a quiet rap record measures as calm, but it does not sound calm to the
    /// listener.</para>
    ///
    /// <para><b>The exception is the whole of the rule.</b> "Just the Bars" is the chill hip-hop row. Rap
    /// is not an intruder there. It is what the row is named after, and the only thing in it. So this is
    /// asked of a POOLED reading and never of <c>MoodList.Within</c>. A list that says what it is about may
    /// hold whatever it says.</para>
    ///
    /// <para>A decade under a mood is pooled too. "Chill 1990s" is not a rap list either, and its
    /// description names a decade rather than a genre.</para>
    ///
    /// <para><b>Focus lists must not hold rap either.</b> This list is for music that stays out of the way.
    /// Somebody talking to you is the one thing that cannot. The meter hears level and events, and it hears
    /// no voice. So a sparse rap record over an even level measures as focus music, and it is the opposite
    /// of that.</para>
    ///
    /// <para>This is empty for every other mood. That is a stated answer, not a gap. Rap belongs in the
    /// loud and danceable lists.</para>
    /// </summary>
    public static IReadOnlySet<string> NotPooledInto(SmartPlaylistKind kind) => kind switch
    {
        SmartPlaylistKind.Calm or SmartPlaylistKind.Focus => Rapping,
        _ => Nothing,
    };

    private static readonly IReadOnlySet<string> Nothing =
        new HashSet<string>(StringComparer.Ordinal);

    /// <summary>How much of a record has to be sleep music before any of it counts.
    ///
    /// <para><b>A listener judges the record. The meter judges the track.</b> Blank Banshee is not sleep
    /// music. Four of its tracks measure as the stillest, darkest, flattest things on a real shelf. They
    /// are the quiet ambient interludes that a vaporwave album puts between its songs. Every number about those
    /// four tracks is correct, and the list they landed in was wrong. A track is offered here only when a
    /// THIRD of its record qualifies too. That is the difference between a record that somebody puts on to
    /// fall asleep and one that happens to contain an interlude.</para>
    ///
    /// <para>On a real collection, a quarter let the vaporwave tracks in. A third keeps them out, and the
    /// list then holds Enya, John Barry, Vangelis and Leonard Cohen.</para>
    /// </summary>
    public const double SleepShareOfRecord = 1.0 / 3.0;

    /// <summary>The fewest tracks of a record that must qualify, whatever the share says. Without it, a
    /// two-track single with one still song on it would be 100 per cent sleep music.</summary>
    public const int SleepLeastOnRecord = 3;

    /// <summary>How many measured tracks a genre needs before it is cut against itself rather than against
    /// the whole collection.
    ///
    /// <para>Forty. At 15 per cent from each end, that is the sixth track in from the end. That is a
    /// percentile with something behind it. At ten tracks, the edge falls on the second track, and it is
    /// then decided by whichever two records happened to be ripped. A listener with nine jazz records has a
    /// jazz shelf, not a jazz distribution. Those nine are better placed against everything they own than
    /// against each other.</para>
    /// </summary>
    public const int LeastForOwnCut = 40;

    /// <summary>The key the whole-collection cut is filed under. It is not a genre, and it cannot collide
    /// with one. Every real key here is a member of <see cref="Genres.All"/>.</summary>
    private const string Everything = "\0all";

    /// <summary>Every genre with enough measured tracks to be its own reference, plus the whole collection
    /// under <see cref="Everything"/>. Never empty: the fallback is always built.</summary>
    private readonly Dictionary<string, Cohort> _cohorts;

    private MoodBands(Dictionary<string, Cohort> cohorts) => _cohorts = cohorts;

    /// <summary>Reads the collection and works out where every band edge falls.
    ///
    /// <para>This is done once for a whole shelf, and then asked per track. The sorting is the cost, and it
    /// is paid here rather than per label.</para>
    /// </summary>
    /// <param name="measured">Every track that has a reading
    /// (<see cref="LibraryDatabase.QueryMeasuredMoods"/>). That query has already dropped the unmeasurable
    /// rows and the tracks too short to be songs. Rows that should not shape a distribution must be kept
    /// out HERE, because an edge is a fact about everything that went into it.</param>
    /// <param name="genreByFolder">What each album folder is filed under, canonical. See
    /// <see cref="GenreByFolder"/>. A folder missing from this table is a record with no genre the app
    /// recognises. Its tracks are cut against the whole collection instead.</param>
    public static MoodBands Cut(
        IReadOnlyList<MeasuredMood> measured,
        IReadOnlyDictionary<string, string> genreByFolder)
    {
        ArgumentNullException.ThrowIfNull(measured);
        ArgumentNullException.ThrowIfNull(genreByFolder);

        var byGenre = new Dictionary<string, List<MeasuredMood>>(StringComparer.Ordinal);
        foreach (var row in measured)
        {
            if (GenreOf(row, genreByFolder) is not { } genre)
                continue;
            if (!byGenre.TryGetValue(genre, out var list))
                byGenre[genre] = list = [];
            list.Add(row);
        }

        var cohorts = new Dictionary<string, Cohort>(StringComparer.Ordinal)
        {
            // Built from everything, including the tracks that also went into a genre of their own. A
            // record is part of the collection whether or not its genre is populous enough to have its own
            // edges. Leaving it out would make the fallback a distribution of the leftovers.
            [Everything] = Cohort.Of(measured),
        };

        foreach (var (genre, rows) in byGenre)
            if (rows.Count >= LeastForOwnCut)
                cohorts[genre] = Cohort.Of(rows);

        return new MoodBands(cohorts);
    }

    /// <summary>What to call one track.
    ///
    /// <para>This takes the whole measured row rather than three doubles. So a caller cannot pass the
    /// numbers in the wrong order.</para>
    /// </summary>
    /// <param name="track">The row as it was measured.</param>
    /// <param name="genreByFolder">The same table that <see cref="Cut"/> was given. It is asked for again
    /// rather than held, because labels are read on a different clock from the cut. A folder can be
    /// retagged between the two. What a record is called should follow what it is filed under NOW, not what
    /// it was filed under when the edges were worked out. A genre that has appeared since then has no
    /// cohort yet, and the fallback already covers it.</param>
    public MoodLabel LabelFor(MeasuredMood track, IReadOnlyDictionary<string, string> genreByFolder)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(genreByFolder);

        var genre = GenreOf(track, genreByFolder);
        var cohort = CohortOf(genre);

        double onsets = cohort.RankOfOnsets(track.Onsets);
        double brightness = cohort.RankOfBrightness(track.Brightness);
        double dynamics = cohort.RankOfDynamics(track.Dynamics);

        // Onsets and brightness, weighted equally. These two make up the arousal axis. Dynamics is not on
        // that axis, which is why it only decides danceable.
        double arousal = (onsets + brightness) / 2;

        var band = arousal <= CalmAt ? MoodBand.Calm
                 : arousal >= EnergeticAt ? MoodBand.Energetic
                 : MoodBand.None;

        // Loud enough to dominate the listening, rather than stay in the background. Null is not loud. See
        // LoudAt, which explains why.
        bool loud = track.Loudness is { } lufs && lufs > LoudAt;

        // Busy OR loud, over a level that holds still. The second arm catches the half-time dance record
        // whose energy is sustained rather than struck. The onset count reads that record as sparse.
        // Without the second arm it would land in Focus, the list at the other end of this same axis.
        bool danceable = (onsets >= BusyAt || loud) && dynamics <= EvenAt;

        // The same fact refuses the track here. That keeps the pair from ever holding one track twice.
        // Focus needs its own way out, to match the second way into Dancefloor.
        bool focused = onsets <= SparseAt && dynamics <= EvenAt && !loud;

        return new MoodLabel(
            band, danceable, genre,
            Confidence(band, arousal, cohort.Count),
            danceable ? Drive(onsets, dynamics) : 0,
            focused, focused ? Stillness(onsets, dynamics) : 0);
    }

    /// <summary>How danceable a danceable track is: the weaker of how far past busy and how far past even
    /// it sits, each measured across the room that condition leaves.
    ///
    /// <para>This is the weaker of the two, for the reason that <see cref="Confidence"/> takes the weaker
    /// of its two. A record that is the busiest thing on the shelf, over a level that only just holds
    /// still, is not more danceable than one that is comfortably both. Taking the smaller keeps the pair
    /// honest. An average would let one condition carry a track that barely met the other.</para>
    /// </summary>
    private static double Drive(double onsets, double dynamics)
    {
        double busy = (onsets - BusyAt) / (1 - BusyAt);
        double even = (EvenAt - dynamics) / EvenAt;
        return Math.Clamp(Math.Min(busy, even), 0, 1);
    }

    /// <summary>How focused a focused track is: the weaker of how far under sparse and how far under even
    /// it sits, each measured across the room that condition leaves.
    ///
    /// <para><see cref="Drive"/>'s mirror, and the weaker of the two for the same reason. A record with
    /// almost no events over a level that only just holds still is not better to work through than one that
    /// is comfortably both. An average would let one condition carry a track that barely met the
    /// other.</para>
    /// </summary>
    private static double Stillness(double onsets, double dynamics)
    {
        double sparse = (SparseAt - onsets) / SparseAt;
        double even = (EvenAt - dynamics) / EvenAt;
        return Math.Clamp(Math.Min(sparse, even), 0, 1);
    }

    /// <summary>How well a track would do as sleep music, 0..1, or nought where it would not do at all.
    ///
    /// <para>This is read against the whole collection and never against a genre. See
    /// <see cref="SleepAt"/>. Three conditions must all hold: few enough events, dark enough, and no swing
    /// that would wake somebody. The result is how far inside the stillest of those the track sits, so a
    /// list can be ordered by it.</para>
    /// </summary>
    public double Sleepiness(MeasuredMood track)
    {
        ArgumentNullException.ThrowIfNull(track);

        // The whole collection, whatever this record is filed under. A genre cohort would defeat the
        // question. The stillest techno on a techno shelf is still techno.
        var everything = _cohorts[Everything];

        double onsets = everything.RankOfOnsets(track.Onsets);
        double brightness = everything.RankOfBrightness(track.Brightness);
        double dynamics = everything.RankOfDynamics(track.Dynamics);

        if (dynamics > SleepSwingAt)
            return 0;

        double stillness = (onsets + brightness) / 2;
        if (stillness > SleepAt)
            return 0;

        // How far below the line it sits, so the stillest record on the shelf leads the list.
        return Math.Clamp((SleepAt - stillness) / SleepAt, 0, 1);
    }

    /// <summary>How much to believe a band: the weaker of the two reasons to doubt it, never their
    /// product.
    ///
    /// <para>One reason is the reading itself. A track at the band edge could just as easily be on the other
    /// side of it, like the track next to it. A track at the very bottom could not. This is measured as
    /// how far into its own band the track sits. The edge is nought, and the extreme is one.</para>
    ///
    /// <para>The other reason is the cut. Edges taken from forty tracks are worth less than edges taken
    /// from four hundred. A cohort that only just reached <see cref="LeastForOwnCut"/> should say so,
    /// rather than answering as confidently as the whole collection does.</para>
    ///
    /// <para>The weaker of the two is used, not both multiplied. These are not independent doubts to be
    /// compounded. They are two separate ways the same answer can be wrong, and a label is worth what its
    /// worst part is worth. Multiplied, a clear reading from a large collection would report as only half
    /// certain.</para>
    /// </summary>
    private static double Confidence(MoodBand band, double arousal, int count)
    {
        if (band == MoodBand.None)
            return 0;

        double intoBand = band == MoodBand.Calm
            ? (CalmAt - arousal) / CalmAt
            : (arousal - EnergeticAt) / (1 - EnergeticAt);

        double behindTheCut = Math.Clamp((double)count / (LeastForOwnCut * 4), 0, 1);

        return Math.Clamp(Math.Min(intoBand, behindTheCut), 0, 1);
    }

    /// <summary>The genre's own edges where it has them, and the whole collection's where it does not. This
    /// is said once, because two readings of "which distribution is this track cut against" would be two
    /// answers that can drift apart.</summary>
    private Cohort CohortOf(string? genre)
        => genre is not null && _cohorts.TryGetValue(genre, out var own) ? own : _cohorts[Everything];

    /// <summary>How much this track sounds like the middle of its own genre, nought to one.
    ///
    /// <para><b>What it is for.</b> A genre row under a mood asks a different question from the pooled list
    /// above it. "The calmest music I own" wants the calmest thing, whatever it is. "The calmest rock I
    /// own" wants that, and it wants the answer to be rock. A four-minute ambient passage on a rock record
    /// is calm, and it is on the rock shelf. It is not what anybody means.</para>
    ///
    /// <para><b>Measured on the axis that the mood is NOT selecting on. This is the whole trick.</b>
    /// Measured on all three axes, this would fight the band it is added to. The calmest rock is by
    /// definition at the far end of its genre's arousal. So the most typical rock would always be the least
    /// calm, and the two terms would cancel. Calm and Energetic are cut on arousal, which is onsets and
    /// brightness. So those two read DYNAMICS. Danceable and Focus are cut on onsets and evenness, so those
    /// read BRIGHTNESS. Either way, the term carries information that the band has not already used. That
    /// is the only way it can add anything.</para>
    ///
    /// <para>The value is nought at either extreme of the genre, and one at its middle. Typical means
    /// ordinary for this shelf, not extreme in a particular direction. The axis is already a percentile
    /// within the genre's own cohort, so "the middle" is the genre's median by construction. A genre too
    /// small for its own cut is measured against the whole collection, exactly as its band is.</para>
    /// </summary>
    /// <param name="kind">Which list is asking. This decides which axis is still free. A kind with no
    /// per-genre form returns nought, because it has no genre row for this to order.</param>
    public double TypicalFor(
        MeasuredMood track, IReadOnlyDictionary<string, string> genreByFolder, SmartPlaylistKind kind)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(genreByFolder);

        var cohort = CohortOf(GenreOf(track, genreByFolder));

        double axis = kind switch
        {
            SmartPlaylistKind.Calm or SmartPlaylistKind.Energetic => cohort.RankOfDynamics(track.Dynamics),
            SmartPlaylistKind.Danceable or SmartPlaylistKind.Focus => cohort.RankOfBrightness(track.Brightness),
            _ => -1,
        };

        return axis < 0 ? 0 : 1 - (2 * Math.Abs(axis - 0.5));
    }

    /// <summary>The canonical genre of the record a track is on, or null where it has none.</summary>
    private static string? GenreOf(MeasuredMood track, IReadOnlyDictionary<string, string> genreByFolder)
        => genreByFolder.TryGetValue(track.Folder, out var genre) ? genre : null;

    /// <summary>Each album folder's genre: the genre that MOST of its tracks are filed under. A tie goes
    /// to the alphabetically first genre, so a record that is half Jazz and half Blues lands somewhere
    /// definite. It does not move between two reads.
    ///
    /// <para><b>Three answers, in order of standing. The order is the whole design.</b> First, a tag that
    /// NAMES a shelf outright is the most authoritative thing there is. It is one value that a person set
    /// or picked, and it says what the record is. Second, the catalogues, for the folders that nobody filed
    /// by hand. They come second and not first, because the person who owns the record outranks a catalogue
    /// about their own copy of it. Third, and only where neither of those said anything, whatever
    /// <see cref="Genres.Match"/> can read out of the tag that named no shelf.</para>
    ///
    /// <para><b>Why the third tier exists.</b> A compound tag left over from another tagger, such as "Rock
    /// | Pop | Folk Rock", is not a shelf name, so it falls through to the catalogues first. Only
    /// if they say nothing is it read for words. Without the third tier, 40% of the measured tracks on a real
    /// collection were cut against
    /// the whole shelf, even though their own tags named a genre. The catalogues answer only for a folder
    /// that has a release link AND an answer behind it. That covers only a fraction of a collection. A
    /// parsed tag is a worse answer than a catalogue's, and a far better one than none.</para>
    ///
    /// <para><b>Folded first and counted second. This is the opposite order to
    /// <c>LibraryAlbumIndex.ReadAlbumGenres</c>.</b> The two answer different questions. That one lists the
    /// genres a listener has typed, so it has to keep their words. This one needs a distribution to cut
    /// against. So a record whose tracks say "Hard Rock", "Rock" and "Album Rock" is one rock record here.
    /// There, it would be three tied minorities.</para>
    ///
    /// <para>A tag that none of the three can place is no genre, rather than a genre of its own. Cutting
    /// the unplaceable tags against each other tells us nothing. They are not a kind of music. They are the
    /// records whose tags did not parse, and they belong against the whole collection.</para>
    /// </summary>
    /// <param name="counts">How many tracks in each folder carry each genre
    /// (<see cref="LibraryDatabase.QueryAlbumGenreCounts"/>).</param>
    /// <param name="factsByFolder">What the catalogues say about each folder's release
    /// (<see cref="LibraryDatabase.CatalogueFactsByFolder"/>). This is read for the folders that the user's
    /// own field did not name outright.</param>
    public static Dictionary<string, string> GenreByFolder(
        IReadOnlyList<(string Folder, string Genre, int Count)> counts,
        IReadOnlyDictionary<string, CatalogueFacts> factsByFolder)
    {
        ArgumentNullException.ThrowIfNull(counts);
        ArgumentNullException.ThrowIfNull(factsByFolder);

        // Two tallies over one pass, because the same tag answers two questions of different standing. One
        // asks whether the tag NAMES a shelf. The other asks, if it does not, whether a shelf can be read
        // out of it.
        var named = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        var read = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

        // A collection carries a few hundred distinct genre spellings across many thousand folders. So each
        // spelling is read once, and the answer is kept for the rest of the pass.
        var readings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (folder, genre, count) in counts)
        {
            var tag = genre?.Trim() ?? "";
            if (tag.Length == 0)
                continue;

            // The user's field, and NOT a parse of it. Genre is one value that a person sets or picks. A
            // value that names a shelf outright is the most authoritative thing in the file.
            if (Shelves.TryGetValue(tag, out var canonical))
            {
                Add(named, folder, canonical, count);
                continue;
            }

            // Where the tag names no shelf, read what can be taken out of it. This goes through the one
            // reader that the whole app uses to file records (Genres.Match). So "R&B / Soul" is an R&B
            // record here, for the same reason that it is one everywhere else. A tag that the vocabulary
            // cannot place is still no genre.
            //
            // This is a tier BELOW the catalogues, not beside the check above. A compound tag left over
            // from another tagger, such as "Rock | Pop | Folk Rock", is a worse answer than a catalogue's.
            // It is a better answer than none. The catalogues reach only a fraction of a collection. Every
            // folder they miss would otherwise be cut against the whole shelf, even though its own tag says
            // what it is.
            if (!readings.TryGetValue(tag, out var reading))
                readings[tag] = reading = Genres.Match(tag);
            if (reading is not null)
                Add(read, folder, reading, count);
        }

        var winner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, perGenre) in named)
            if (Majority(perGenre) is { } best)
                winner[folder] = best;

        // What the catalogues say, for the folders that nobody filed by hand. This is second and not first.
        // The person who owns the record outranks the catalogue about their own copy of it.
        foreach (var (folder, facts) in factsByFolder)
            if (facts.Genre is { Length: > 0 } genre && !winner.ContainsKey(folder))
                winner[folder] = genre;

        // Last, and only where the first two said nothing at all.
        //
        // A folder that none of the three answers for is NOT in the map. That is the whole answer. It has
        // no cohort of its own, and it is cut against the collection, exactly as a genre with fewer than
        // LeastForOwnCut tracks already is. The data is there, or it is not.
        foreach (var (folder, perGenre) in read)
            if (!winner.ContainsKey(folder) && Majority(perGenre) is { } best)
                winner[folder] = best;

        return winner;

        static void Add(
            Dictionary<string, Dictionary<string, int>> tally, string folder, string genre, int count)
        {
            if (!tally.TryGetValue(folder, out var perGenre))
                tally[folder] = perGenre = new Dictionary<string, int>(StringComparer.Ordinal);
            perGenre[genre] = perGenre.TryGetValue(genre, out var running) ? running + count : count;
        }

        // The genre that most of the folder's tracks carry. The name breaks a tie, so two reads of an
        // unchanged folder cannot file it under two different shelves.
        static string? Majority(Dictionary<string, int> perGenre)
        {
            string? best = null;
            int bestCount = 0;
            foreach (var (genre, count) in perGenre)
                if (count > bestCount || (count == bestCount && string.CompareOrdinal(genre, best) < 0))
                    (best, bestCount) = (genre, count);
            return best;
        }
    }

    /// <summary>The shelf names, by name, for the exact test above. Built once. This is asked per folder,
    /// per genre, on every cut.</summary>
    private static readonly Dictionary<string, string> Shelves =
        Genres.All.ToDictionary(g => g, g => g, StringComparer.OrdinalIgnoreCase);

    /// <summary>One reference distribution: the three measurements of some set of tracks, each sorted. So
    /// a track's rank among them is a binary search rather than a full pass.
    /// </summary>
    private sealed class Cohort
    {
        private readonly double[] _brightness, _onsets, _dynamics;

        private Cohort(double[] brightness, double[] onsets, double[] dynamics)
            => (_brightness, _onsets, _dynamics) = (brightness, onsets, dynamics);

        public int Count => _brightness.Length;

        public static Cohort Of(IReadOnlyList<MeasuredMood> rows)
        {
            var brightness = new double[rows.Count];
            var onsets = new double[rows.Count];
            var dynamics = new double[rows.Count];
            for (int i = 0; i < rows.Count; i++)
                (brightness[i], onsets[i], dynamics[i]) =
                    (rows[i].Brightness, rows[i].Onsets, rows[i].Dynamics);

            Array.Sort(brightness);
            Array.Sort(onsets);
            Array.Sort(dynamics);
            return new Cohort(brightness, onsets, dynamics);
        }

        public double RankOfBrightness(double value) => Rank(_brightness, value);
        public double RankOfOnsets(double value) => Rank(_onsets, value);
        public double RankOfDynamics(double value) => Rank(_dynamics, value);

        /// <summary>Where a value sits in a sorted population, 0..1, counting a tie as half.
        ///
        /// <para>This is the midpoint of the run of equal values, not either of its ends. That matters more
        /// here than it usually would. Dynamics is nought for every fully compressed record, and a real
        /// collection holds a great many of them. Ranked at the bottom of their own tie, they would all
        /// read as the most even thing on the shelf. Ranked at the top, none of them would be even at
        /// all.</para>
        ///
        /// <para>An empty population ranks everything in the middle. That is the answer that gives no band.
        /// A cohort is never built empty. A caller that reaches this with nothing should get no opinion,
        /// not an arbitrary one.</para>
        /// </summary>
        private static double Rank(double[] sorted, double value)
        {
            if (sorted.Length == 0)
                return 0.5;

            // First index whose value is not less than this one, then the first past the run of equals.
            int low = 0, high = sorted.Length;
            while (low < high)
            {
                int mid = (low + high) / 2;
                if (sorted[mid] < value) low = mid + 1; else high = mid;
            }

            int firstEqual = low;
            high = sorted.Length;
            while (low < high)
            {
                int mid = (low + high) / 2;
                if (sorted[mid] <= value) low = mid + 1; else high = mid;
            }

            return (firstEqual + low) / 2.0 / sorted.Length;
        }
    }
}

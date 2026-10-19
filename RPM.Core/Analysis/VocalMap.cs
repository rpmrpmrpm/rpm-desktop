using RPM.Core.Lyrics;

namespace RPM.Core.Analysis;

/// <summary>
/// Where somebody is singing on one record, and separately whether that is known at all.
///
/// <para><b>Why a type and not a curve.</b> Two records can share drums and cannot share voices, so a
/// changeover needs to know where the singing is on BOTH of them. The mid band already gives a rough
/// answer (<see cref="TrackAnalysis.VocalCurve"/>), and it is rough in a way that matters: it is the
/// middle of the record's spectrum, so a lead guitar, a talkbox and a vocal pad all read as somebody
/// singing. A lyric sheet, where there is one, gives the exact answer at no cost, because the stamps
/// are already on disk. This type is the one door to that answer, and it is the same door whichever
/// of the two is behind it, so the planner never carries a "did we get the good one" flag around.</para>
///
/// <para><b>The distinction the whole thing turns on.</b> "Nobody has written this record down" and
/// "nobody sings on this record" are different facts, and an instrumental is the BEST record to mix
/// under. If a missing sheet quietly read as silence, every record without a sheet would become a
/// place to lay a vocal over. On a hundred-track dance compilation, a quarter of the missing sheets
/// were instrumentals and the rest were records with words that nobody had transcribed. So
/// <see cref="Unknown"/> is a state of its own and cannot be read as either. A caller holding one has
/// to go and get the bands, which is what <see cref="FromBands"/> is for.</para>
///
/// <para>Sung stretches are BOUNDS, not measurements. A sheet says when a line starts and never when
/// it stops, so a line is given as much record as it could possibly need at
/// <see cref="LyricWindow.SlowestChars"/>, and no more. This is what makes it work on the records that
/// get mixed. Dance sheets are sparse: two stamps a minute apart with four seconds of words between
/// them read as four seconds of singing and fifty-six of breakdown, which is exactly the stretch a DJ
/// is looking for.</para>
/// </summary>
public sealed class VocalMap
{
    /// <summary>How much is added to each end of a sung stretch. Singers come in ahead of a hand-written
    /// stamp (the same slop <see cref="LyricWindow.Slack"/> exists for). The cost of being wrong is
    /// not symmetric: a changeover that starts a beat inside a vocal is the fault this prevents,
    /// and one that starts a beat later than it had to is something nobody hears.</summary>
    public const double Margin = 0.6;

    /// <summary>Two sung stretches closer than this are one. A gap of a bar is a breath, not a place to
    /// bring another record in, and leaving it in the map would invite a fade to be planned through it.</summary>
    public const double LeastGap = 2.0;

    /// <summary>Where the answer came from. The trace prints it and nothing else branches on it: the
    /// planner asks the same questions whichever one it holds.</summary>
    public enum Told
    {
        /// <summary>Nobody has said. Not the same as silence: see <see cref="FromBands"/>.</summary>
        Nothing,

        /// <summary>Read from a lyric sheet's stamps.</summary>
        Sheet,

        /// <summary>Guessed from the middle of the record's own spectrum.</summary>
        Bands,

        /// <summary>Both readings, and only where they agree (see <see cref="Both"/>). The planner
        /// places a changeover by this on any record that has been both written down and
        /// measured.</summary>
        Agreed,
    }

    private readonly (double From, double To)[] _sung;

    private VocalMap(Told from, (double From, double To)[] sung)
    {
        Source = from;
        _sung = sung;
    }

    /// <summary>No sheet, no bands, nothing measured. A caller that reaches this has to decide what to do
    /// about it rather than being handed a silence it did not earn.</summary>
    public static readonly VocalMap Unknown = new(Told.Nothing, []);

    public Told Source { get; }

    /// <summary>Whether anything is known about this record at all.</summary>
    public bool IsKnown => Source != Told.Nothing;

    /// <summary>Nobody sings on this record: known, and empty. It is the best kind of record to mix
    /// under, and the reason <see cref="Unknown"/> is a separate state.</summary>
    public bool IsInstrumental => IsKnown && _sung.Length == 0;

    /// <summary>The stretches somebody is singing in, in the record's own clock, in order and merged.</summary>
    public IReadOnlyList<(double From, double To)> Sung => _sung;

    /// <summary>
    /// The map a lyric sheet gives. A sheet with stamps but no words on any of them (a writer marking a
    /// record as instrumental) comes back <see cref="IsInstrumental"/>, which is a real answer.
    /// </summary>
    /// <param name="sheet">The sheet. An unsynced one has no stamps to read, so it says nothing and
    /// comes back <see cref="Unknown"/>.</param>
    public static VocalMap FromSheet(LyricSheet? sheet)
    {
        if (sheet is not { IsSynced: true })
            return Unknown;

        var found = new List<(double From, double To)>();

        for (var i = 0; i < sheet.Lines.Count; i++)
        {
            var line = sheet.Lines[i];
            if (LyricSheet.IsWordless(line.Text))
                continue;                       // the writer saying nobody sings here

            var next = i + 1 < sheet.Lines.Count ? sheet.Lines[i + 1].Seconds : double.PositiveInfinity;
            if (Span(line, next) is { } span)
                found.Add(span);
        }

        return new VocalMap(Told.Sheet, Merge(found));
    }

    /// <summary>
    /// One line's stretch: it starts where the line starts, and ends at the earlier of two times:
    /// when its own words could finish, and where the next line begins.
    ///
    /// <para>Word stamps are used where the sheet carries them, since then both ends are stated rather
    /// than bounded. Most sheets do not carry them.</para>
    /// </summary>
    private static (double From, double To)? Span(LyricLine line, double next)
    {
        double from = line.Seconds, to;

        if (line.Words is { Count: > 0 } words)
        {
            // A word-stamped sheet says when the last word STARTS, not when it ends, so the last word
            // still needs the same bound the whole line would have got.
            var last = words[^1];
            from = Math.Min(from, words[0].Seconds);
            to = last.Seconds + Length(last.Text) / LyricWindow.SlowestChars;
        }
        else
        {
            to = from + Length(LyricSheet.Unstamp(line.Text)) / LyricWindow.SlowestChars;
        }

        // Never past where the next line starts: that is the sheet's own bound, and the tighter of
        // the two on any line that is not the last. The same reasoning as LyricWindow's far wall.
        to = Math.Min(to, next);
        if (to <= from)
            return null;

        return (Math.Max(0, from - Margin), to + Margin);
    }

    /// <summary>How many characters of a line are actually sung. Whitespace is not, and a line that is
    /// all punctuation is not either.</summary>
    private static int Length(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var count = 0;
        foreach (var c in text)
            if (char.IsLetterOrDigit(c))
                count++;
        return count;
    }

    /// <summary>
    /// The map the middle of the record's own spectrum gives, for a record nobody has written down.
    ///
    /// <para>It is rough, and known to be rough (a lead line reads as a voice). It is the same curve
    /// <see cref="MixOutFinder"/> uses, so a record with no sheet is read no worse than it would be
    /// without this type.</para>
    ///
    /// <para><b>The cut is placed between the record's own two levels, not at its median.</b> The
    /// median is what a record does MOST of the time, and on the records that get mixed that is the
    /// breakdown rather than the vocal. A house track that sings over a quarter of its length has a
    /// median in the instrumental, and every reading above it, including the whole quiet three
    /// quarters, would then read as somebody singing. So the levels are taken at the two ends and the
    /// line is drawn between them, which lands in the same place whether a record sings for a quarter
    /// of itself or three quarters.</para>
    ///
    /// <para>A record with no contrast between its two ends is called sung THROUGHOUT. The band cannot
    /// tell a record with a voice on every bar from one with no voice at all, because both are flat,
    /// and only one of the two guesses is safe. "Singing everywhere" makes this record no help to the
    /// planner, which is the same as having no reading. "Instrumental everywhere" would make it the
    /// first choice to lay a vocal over.</para>
    /// </summary>
    public static VocalMap FromBands(TrackAnalysis? analysis)
    {
        if (analysis?.VocalCurve is not { Count: > 0 } voice)
            return Unknown;

        var step = EnergyProfile.StepSeconds;

        var low = Percentile(voice, Quiet);
        var high = Percentile(voice, Loud);
        if (high <= 0)
            return Unknown;                     // nothing to judge a reading against

        if (high - low < high * LeastContrast)
            return new VocalMap(Told.Bands, [(0, voice.Count * step)]);

        var loud = low + (high - low) * 0.5;

        var found = new List<(double From, double To)>();
        var open = -1;

        for (var i = 0; i < voice.Count; i++)
        {
            var singing = voice[i] > loud;
            if (singing && open < 0)
                open = i;
            else if (!singing && open >= 0)
            {
                found.Add((Math.Max(0, open * step - Margin), i * step + Margin));
                open = -1;
            }
        }

        if (open >= 0)
            found.Add((Math.Max(0, open * step - Margin), voice.Count * step + Margin));

        return new VocalMap(Told.Bands, Merge(found));
    }

    /// <summary>
    /// Where two readings of the same record BOTH say somebody is singing: the overlap, and nothing
    /// either of them claims on its own.
    ///
    /// <para>A lyric sheet is a stranger's transcription with a stranger's timings on it, and most of
    /// them are a second or two out. The band underneath is a measurement of the middle of the
    /// spectrum, which calls a lead line a voice. Each is wrong somewhere the other is not, so where
    /// the two land on the same stretch of record there is very little room left for either to be
    /// wrong. That stretch is the one worth trusting, and it is the only thing this returns.</para>
    ///
    /// <para>The result is Unknown unless both inputs are known. This is deliberate: an overlap with a
    /// reading that does not exist is empty, and an empty map is <em>instrumental</em>, so a record
    /// nobody has written down would become a place to lay a vocal over. Two known readings that share
    /// nothing ARE an honest instrumental, because both of them were asked.</para>
    ///
    /// <para>Nothing plans from this on its own. It is drawn beside the two readings it is made of, so
    /// that a sheet whose timings have drifted off the song can be seen to have drifted.</para>
    /// </summary>
    public static VocalMap Both(VocalMap? a, VocalMap? b)
    {
        if (a is not { IsKnown: true } left || b is not { IsKnown: true } right)
            return Unknown;

        var found = new List<(double From, double To)>();

        // Both lists are in order and neither is long (a record has tens of sung stretches), so this
        // walks them together rather than pairing every span with every other.
        int i = 0, j = 0;
        while (i < left._sung.Length && j < right._sung.Length)
        {
            var (fromA, toA) = left._sung[i];
            var (fromB, toB) = right._sung[j];

            double from = Math.Max(fromA, fromB);
            double to = Math.Min(toA, toB);
            if (to > from)
                found.Add((from, to));

            // Step whichever ends first: the other may still overlap what comes after it.
            if (toA < toB)
                i++;
            else
                j++;
        }

        // Merged, but NOT widened again. The margin either reading carries is already inside the
        // spans being overlapped, and adding it a third time would grow the one band that exists to
        // be the strict answer.
        return new VocalMap(Told.Agreed, Merge(found));
    }

    /// <summary>
    /// A record taken to sing from end to end, which is how anybody who must act on an
    /// <see cref="Unknown"/> record has to treat it.
    ///
    /// <para><b>This is an assumption, and the name says so.</b> <see cref="Unknown"/> holds no
    /// stretches, so a consumer that reads it as a map rather than as a state finds a record that
    /// sings nowhere. An unread record would then be the safest record to lay a vocal over, which is
    /// the exact failure <see cref="Unknown"/> exists to prevent. The safe reading is the opposite
    /// one, and it has to be asked for rather than fallen into. "Singing everywhere" costs a
    /// changeover the chance to improve itself. "Singing nowhere" costs it the fault it was
    /// avoiding.</para>
    ///
    /// <para>The source stays <see cref="Told.Nothing"/> on purpose, so a trace can still say that
    /// nobody read this record, rather than claim it was measured to sing.</para>
    /// </summary>
    public static VocalMap SingingThroughout(double durationSeconds) =>
        durationSeconds > 0 ? new VocalMap(Told.Nothing, [(0, durationSeconds)]) : Unknown;

    /// <summary>
    /// What is known about a record: its sheet where it has one, the bands where it does not, and
    /// <see cref="Unknown"/> where neither has been read.
    ///
    /// <para>Both arguments are required and neither has a default. A missing sheet is something a
    /// caller has to pass explicitly, so that the compiler asks every caller whether it went and
    /// looked.</para>
    /// </summary>
    public static VocalMap For(TrackAnalysis? analysis, LyricSheet? sheet)
    {
        var told = FromSheet(sheet);
        var measured = FromBands(analysis);

        // Where the record has been both written down and measured, the changeover is placed by the
        // stretch the two AGREE on (see Both).
        //
        // A sheet alone is a stranger's transcription with a stranger's timings on it: most of them
        // run a second or two out and some are out by a phrase, which is enough to put a changeover
        // over the very thing it was moved to avoid. The band alone is no better in the other
        // direction, because it calls a lead line a voice. Neither is the truth, and where they land
        // on the same stretch of record there is very little room left for either to be wrong.
        //
        // Note which way this cuts. The agreed reading is NARROWER than the sheet, so the planner
        // believes there is less singing than the sheet claims: a vocal the band did not hear is one
        // the changeover may be laid over. That is the price of not trusting the timings alone. A mix
        // moved to the wrong place by a drifted stamp is moved for nothing, and it loses the record
        // it was leaving as well.
        if (told.IsKnown && measured.IsKnown)
            return Both(told, measured);

        return told.IsKnown ? told : measured;
    }

    // ── reading it ──

    /// <summary>How many seconds of the stretch <paramref name="from"/> to <paramref name="from"/> plus
    /// <paramref name="seconds"/> have somebody singing in them. Zero for a record nobody has read,
    /// which is why a caller has to know whether it is holding one.</summary>
    public double SungSecondsIn(double from, double seconds)
    {
        if (seconds <= 0 || _sung.Length == 0)
            return 0;

        var to = from + seconds;
        double sung = 0;

        foreach (var (start, end) in _sung)
        {
            if (end <= from)
                continue;
            if (start >= to)
                break;                          // in order, so nothing after this one can overlap either

            sung += Math.Min(end, to) - Math.Max(start, from);
        }

        return sung;
    }

    /// <summary>Whether anybody sings anywhere in a stretch.</summary>
    public bool SingsIn(double from, double seconds) => SungSecondsIn(from, seconds) > 0;

    /// <summary>
    /// The sung stretches inside a window, clipped to it and moved onto the window's own clock, where
    /// zero is the moment the window opens.
    ///
    /// <para>This is what lets two records be compared. A changeover runs on ONE clock while the two
    /// records are at different points of their own, so both have to be put on the changeover's clock
    /// before anything can be said about them together. See <see cref="Mixing.MixVoices"/>.</para>
    /// </summary>
    public List<(double From, double To)> Within(double from, double seconds)
    {
        var found = new List<(double From, double To)>();
        if (seconds <= 0)
            return found;

        var to = from + seconds;
        foreach (var (start, end) in _sung)
        {
            if (end <= from)
                continue;
            if (start >= to)
                break;

            found.Add((Math.Max(start, from) - from, Math.Min(end, to) - from));
        }

        return found;
    }

    // ── plumbing ──

    /// <summary>In order, with touching and overlapping stretches (and stretches a very short gap apart)
    /// run together, so that reading the map is a single walk.</summary>
    private static (double From, double To)[] Merge(List<(double From, double To)> spans)
    {
        if (spans.Count == 0)
            return [];

        spans.Sort((a, b) => a.From.CompareTo(b.From));

        var merged = new List<(double From, double To)> { spans[0] };
        foreach (var span in spans)
        {
            var last = merged[^1];
            if (span.From - last.To <= LeastGap)
                merged[^1] = (last.From, Math.Max(last.To, span.To));
            else
                merged.Add(span);
        }

        return [.. merged];
    }

    /// <summary>Where the record's quiet level is read. It is taken a little above the bottom, not at
    /// it, so that one silent bar between tracks does not become "what this record does when nobody
    /// sings".</summary>
    private const double Quiet = 0.2;

    /// <summary>Where the record's loud level is read, a little below the top for the same reason.</summary>
    private const double Loud = 0.8;

    /// <summary>How far apart those two have to be, as a share of the loud one, before the band is
    /// believed to be telling a voice from no voice at all. Under this the record is flat and the band
    /// is saying nothing.</summary>
    private const double LeastContrast = 0.25;

    /// <summary>One reading from the sorted curve, by share rather than by rank.</summary>
    private static double Percentile(IReadOnlyList<float> curve, double share)
    {
        var sorted = new float[curve.Count];
        for (var i = 0; i < curve.Count; i++)
            sorted[i] = curve[i];
        Array.Sort(sorted);

        var at = (int)Math.Clamp(Math.Round(share * (sorted.Length - 1)), 0, sorted.Length - 1);
        return sorted[at];
    }
}

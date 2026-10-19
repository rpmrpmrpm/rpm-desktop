using RPM.Core.Analysis;

namespace RPM.Core.Viz;

/// <summary>Where the record is, counted in beats rather than in seconds. <see cref="Measured"/> says
/// whether that count came from the track's own grid, or from a clock standing in for one.</summary>
public readonly record struct BeatClock(double Beats, int BeatsPerBar, bool Measured)
{
    /// <summary>The tempo that the stand-in clock runs at, for a record that nobody has analysed. It is not
    /// a guess at this record's tempo, because there is no such reading. It is a pace to cut at that
    /// suits most records.
    /// </summary>
    public const double FreeBpm = 120;

    /// <summary>The clock for a record with no grid: seconds, counted as beats at a steady pace. The
    /// picture still cuts and still moves. It is just not cutting to anything.</summary>
    public static BeatClock Free(double seconds) => new(seconds * (FreeBpm / 60.0), 4, false);

    /// <summary>
    /// Where <paramref name="seconds"/> falls in the record's own count of beats. Zero is the first bar
    /// line. So a bar always starts at a multiple of <see cref="BeatsPerBar"/>, and the run-up to the
    /// first bar line counts backwards from zero.
    ///
    /// <para>The tracked beats are used when there are any. A record that drifts is exactly the record
    /// that a straight multiplication by the tempo walks away from. Failing that, the tempo is used.
    /// Failing that, the stand-in clock is used.</para>
    /// </summary>
    public static BeatClock For(TrackAnalysis? analysis, double seconds)
    {
        if (analysis is not { } a)
            return Free(seconds);

        int perBar = Math.Max(1, a.BeatsPerBar);

        if (a.BeatTimes.Count >= 2)
            return new BeatClock(BeatAt(a, seconds) - a.FirstDownbeat, perBar, true);

        if (a.Bpm > 0)
            return new BeatClock(seconds * (a.Bpm / 60.0), perBar, true);

        return Free(seconds);
    }

    /// <summary>The fractional index into the tracked beats at this instant. For example, 3.5 is halfway
    /// between the fourth beat and the fifth. Before the first beat and after the last, the grid's own
    /// spacing at that end carries the count on. So the picture keeps moving through an intro and an
    /// outro.</summary>
    private static double BeatAt(TrackAnalysis a, double seconds)
    {
        var beats = a.BeatTimes;
        int last = beats.Count - 1;

        if (seconds <= beats[0])
        {
            double first = Math.Max(1e-6, beats[1] - beats[0]);
            return (seconds - beats[0]) / first;
        }

        if (seconds >= beats[last])
        {
            double final = Math.Max(1e-6, beats[last] - beats[last - 1]);
            return last + (seconds - beats[last]) / final;
        }

        // The beats are in order, so a binary search finds the beat before this instant.
        int lo = 0, hi = last;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (beats[mid] <= seconds) lo = mid; else hi = mid;
        }

        double span = Math.Max(1e-6, beats[hi] - beats[lo]);
        return lo + (seconds - beats[lo]) / span;
    }
}

/// <summary>
/// One arrangement of the space that a marched visualiser draws: how many times it is folded around the
/// axis, how far the fold pushes the walls out, how hard it twists as it runs away, how often the cells
/// repeat, and how much metal there is. A room is a hash of the section it belongs to. So the same phrase
/// of the same record is always the same room.
///
/// <para>The six numbers are deliberately not named after any one picture. The tunnel reads them as its
/// struts and rungs, the kaleidoscope reads them as its mirrors, and the lattice reads them as its cell.
/// So the same phrase of the same record changes all three at the same moment. That is why they share
/// this.</para>
/// </summary>
/// <param name="Folds">Repeats around the axis, 3..9. Three is a corridor. Nine is a kaleidoscope.</param>
/// <param name="Arm">How far out the fold pushes the walls, in units.</param>
/// <param name="Twist">Rotation per unit of depth, radians. The picture corkscrews at this rate.</param>
/// <param name="Ring">How often the rungs repeat down the tunnel, in units.</param>
/// <param name="Girth">Half-thickness of the metal.</param>
/// <param name="Cross">How much of each cell the cut-out cross takes back out of it.</param>
public readonly record struct VizRoom(
    float Folds,
    float Arm,
    float Twist,
    float Ring,
    float Girth,
    float Cross);

/// <summary>One camera set-up, and the room it is looking at. Everything a marched visualiser needs for a
/// frame is worked out from where the record is. Nothing is accumulated frame by frame. So a seek, a
/// pause or a dropped frame lands on the same picture that the same instant gives anywhere else.</summary>
/// <param name="Now">The room this section is in.</param>
/// <param name="Was">The room the section before it was in.</param>
/// <param name="Blend">0 at a section's first beat, 1 once it has settled. This is how far the walls have
/// moved from <paramref name="Was"/> to <paramref name="Now"/>. A change of room is a fold count sliding
/// into another, not a cut. It is the one thing on screen that does not happen on the beat.</param>
/// <param name="Palette">Which colour ramp this section is lit with.</param>
/// <param name="Orbit">The camera's roll about the tunnel's axis, in radians.</param>
/// <param name="Advance">How far the camera travels per beat, in units, in this shot.</param>
/// <param name="Travelled">How far it has travelled altogether, in units. This is not <see cref="Advance"/>
/// times <see cref="Beats"/>. A cut picks a new speed. If the whole count were multiplied by the new
/// speed, the camera would move to where it would have been had it always gone that fast. That is a
/// jump of tens of units on every cut. This value is the speeds actually flown, added up.</param>
/// <param name="Fov">Field of view, as the distance to the projection plane.</param>
/// <param name="Sway">How far the aim wanders off the axis.</param>
/// <param name="Beats">Beats since the first bar line, fractional.</param>
/// <param name="Pulse">1 on the beat, falling to 0 by the next one.</param>
/// <param name="Drop">1 where the beat has just come back after a breakdown, falling to 0 over the bars
/// after it.</param>
public readonly record struct VizShot(
    VizRoom Now,
    VizRoom Was,
    float Blend,
    int Palette,
    float Orbit,
    float Advance,
    float Travelled,
    float Fov,
    float Sway,
    float Beats,
    float Pulse,
    float Drop);

/// <summary>
/// The cutting room, shared by the marched visualisers. It holds no state and draws nothing. Given where
/// the record is, it says where the camera is standing, which arrangement of the space is up, and how
/// hard this instant is hitting.
///
/// <para>The shape of the edit is the record's own. The camera cuts every <see cref="BeatsPerCut"/>
/// beats, and the walls move every <see cref="BarsPerSection"/> bars. So on a record with a grid, every
/// change lands on a beat, and most of them land on a bar line. What each cut goes to is a hash of the
/// cut's number. So the same bar of the same record always cuts to the same camera. That is what makes
/// an edit read as an edit, not as a shuffle.</para>
///
/// <para>This is kept here, and it is pure, so the edit can be checked without a surface. It is the same
/// reason that <see cref="RibbonSea"/> and <see cref="ChladniPlate"/> live here.</para>
/// </summary>
public static class VizDirector
{
    /// <summary>Colour ramps to light a section with.</summary>
    public const int Palettes = 4;

    /// <summary>Beats between camera cuts. Two bars of four.</summary>
    public const int BeatsPerCut = 8;

    /// <summary>Bars between changes of room. Eight is the phrase most records are built in.</summary>
    public const int BarsPerSection = 8;

    /// <summary>How long the walls take to move to the new room, in beats.</summary>
    private const double BlendBeats = 2;

    /// <summary>How long a drop keeps the room wide open, in beats.</summary>
    private const double DropBeats = 8;

    /// <summary>
    /// The frame's shot.
    /// </summary>
    /// <param name="clock">Where the record is, in beats.</param>
    /// <param name="bpm">The record's tempo, or 0 when it has none. The camera travels a set distance per
    /// BEAT, so this only decides how fast the beats themselves arrive.</param>
    /// <param name="beatsSinceDrop">Beats since the beat came back in, or a negative number where the
    /// record has no drops (or has not reached one). Only the first <see cref="DropBeats"/> count.</param>
    public static VizShot Shot(in BeatClock clock, double bpm, double beatsSinceDrop)
    {
        double beats = clock.Beats;
        int perBar = Math.Max(1, clock.BeatsPerBar);
        double perSection = perBar * (double)BarsPerSection;

        // Use floor, not truncation. Before the first bar line the count is negative. Truncation would
        // hold cut 0 for twice as long as every other cut, and the edit would mirror around zero.
        int cut = (int)Math.Floor(beats / BeatsPerCut);
        int section = (int)Math.Floor(beats / perSection);

        float a = Hash(cut, 0), b = Hash(cut, 1), c = Hash(cut, 2);

        // Where we are in the beat, as a fall from 1 to 0. This is the pulse that everything on screen
        // is struck by.
        double intoBeat = beats - Math.Floor(beats);
        float pulse = (float)((1.0 - intoBeat) * (1.0 - intoBeat));

        float drop = beatsSinceDrop < 0 || beatsSinceDrop > DropBeats
            ? 0f
            : (float)(1.0 - beatsSinceDrop / DropBeats);

        double intoSection = beats - section * perSection;
        float blend = (float)Math.Clamp(intoSection / BlendBeats, 0, 1);

        return new VizShot(
            Now: Room(section),
            Was: Room(section - 1),
            Blend: blend,
            Palette: Mod(Hash32(section, 11), Palettes),
            Orbit: (a - 0.5f) * 2.4f,
            // Advance is the shot's own speed. A drop does not change it. A drop only changes the aim (see
            // Sway). The speed is NOT folded into the distance travelled, for the same reason a cut is not.
            // A speed that changed over time, multiplied by the whole count, would move the camera
            // backwards.
            Advance: Speed(cut),
            Travelled: Travelled(beats),
            Fov: 0.55f + 0.45f * c,
            // A drop opens the shot out. The aim stops wandering, and the camera looks down the barrel.
            Sway: (1f - drop) * (0.08f + 0.5f * Hash(cut, 3)),
            Beats: (float)beats,
            Pulse: pulse,
            Drop: drop);
    }

    /// <summary>How fast this shot flies, in units per beat. It is a hash of the cut. So a shot keeps its
    /// speed, and the next shot has its own.</summary>
    private static float Speed(int cut) => 0.9f + 1.4f * Hash(cut, 1);

    /// <summary>
    /// How far the camera has flown by this beat: every completed shot at the speed it was flown at, plus
    /// how far into the current shot we are. It is derived, like everything else here. So a seek arrives
    /// at the same place that playing through would have. It is derived by adding up, not by multiplying.
    /// That is the whole point.
    ///
    /// <para>The loop is over shots, not beats. A shot is two bars, so a long record means a few hundred
    /// turns of the loop each frame. The cap is a safety limit against a position that has gone wrong. It
    /// is not a real limit. An hour at 200 bpm is 1500 shots.</para>
    /// </summary>
    public static float Travelled(double beats)
    {
        int cut = (int)Math.Floor(beats / BeatsPerCut);
        int span = Math.Clamp(cut, -64, 20000);

        double sum = 0;
        if (span >= 0)
            for (int i = 0; i < span; i++) sum += Speed(i);
        else
            for (int i = span; i < 0; i++) sum -= Speed(i);
        sum *= BeatsPerCut;

        return (float)(sum + Speed(span) * (beats - span * (double)BeatsPerCut));
    }

    /// <summary>The arrangement this section is in. Every number is a hash of the section, so a room can be
    /// reproduced exactly. The eighth phrase of a record is the same on every run.</summary>
    public static VizRoom Room(int section) => new(
        // Rounded, so the fold count is a whole number of repeats. A fraction leaves a seam where the
        // last wedge meets the first, running the length of the tunnel.
        Folds: MathF.Round(3f + 6f * Hash(section, 21)),
        Arm: 2.2f + 2.6f * Hash(section, 22),
        Twist: 0.05f + 0.30f * Hash(section, 23),
        Ring: 1.6f + 2.8f * Hash(section, 24),
        Girth: 0.035f + 0.075f * Hash(section, 25),
        Cross: 0.25f + 0.95f * Hash(section, 26));

    /// <summary>Beats from the last drop at or before this instant, or -1 where there is none. The drops
    /// are in order, so this is a walk rather than a search.</summary>
    public static double BeatsSinceDrop(IReadOnlyList<double> dropSeconds, double seconds, double bpm)
    {
        if (dropSeconds.Count == 0 || bpm <= 0)
            return -1;

        double last = double.NaN;
        for (int i = 0; i < dropSeconds.Count; i++)
        {
            if (dropSeconds[i] > seconds) break;
            last = dropSeconds[i];
        }

        return double.IsNaN(last) ? -1 : (seconds - last) * (bpm / 60.0);
    }

    /// <summary>A number in 0..1 for this section (or cut) and this question. It is deterministic. It is
    /// spread enough that two questions about the same number do not give the same answer.</summary>
    private static float Hash(int n, int which) => (Hash32(n, which) & 0xFFFFFF) / (float)0x1000000;

    private static uint Hash32(int n, int which)
    {
        // An integer mix, not a sine function. Every machine gives the same result to the bit. So the
        // tests can say what a given phrase looks like without running a GPU.
        unchecked
        {
            uint h = (uint)n * 2654435761u + (uint)which * 2246822519u;
            h ^= h >> 15;
            h *= 2246822519u;
            h ^= h >> 13;
            h *= 3266489917u;
            h ^= h >> 16;
            return h;
        }
    }

    private static int Mod(uint h, int n) => (int)(h % (uint)n);
}

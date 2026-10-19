using RPM.Core.Metadata;

namespace RPM.Core.Analysis;

/// <summary>
/// What else a record's tempo could be called.
///
/// <para>The octave at which a tempo is counted is a matter of counting, not of speed: a record is
/// not faster because it is called 140 instead of 70, and both are correct. A beat model picks one,
/// and on many records it picks the one that the record's owner would not choose. This cannot be
/// measured as an error, because nothing in the audio shows which of the two counts a listener
/// uses. So the app does not rewrite the number. It shows what the other count would be and lets
/// the person decide.</para>
///
/// <para><b>Why the app does not decide on its own.</b> Folding every tempo into 70..180 was
/// measured by <c>tools/TempoBench</c> over 1697 annotated tracks: the fold fired 225 times,
/// corrected 73 readings and broke 126. For this reason the bottom of the window is not folded (see
/// <see cref="TempoEstimator.FoldDown"/>), and this class asks the person a question instead of
/// giving an answer.</para>
///
/// <para>The genre is the only context used. Nothing in the signal separates 70 from 140, but a
/// hip-hop record is counted at 90 and a house record at 125, and RPM users set a genre readily
/// because the library is organised by it. A wrong genre only causes a hint that should not have
/// appeared. It never changes a tempo.</para>
/// </summary>
public static class TempoOctave
{
    /// <summary>
    /// Where each kind of music is counted, in BPM.
    ///
    /// <para>The bands are wide on purpose, because they are only used to raise a question. A band is
    /// useful when a reading that is an octave out falls outside it and the other count falls inside
    /// it, which is all that is asked. A band tighter than the music does not find more octave
    /// errors. It only creates hints on records that were read correctly.</para>
    ///
    /// <para>The keys come from <see cref="Genres.All"/>. A file tagged "edm" arrives here as
    /// Electronic / Dance through <see cref="Genres.Match"/>, and one tagged "Deep House" arrives as
    /// House.</para>
    /// </summary>
    private static readonly Dictionary<string, (double Low, double High)> Bands = new(StringComparer.Ordinal)
    {
        // Counted at the rapper's pace, not the hi-hat's. The double-time reading is the most common
        // octave error: trap is written at 140 and counted at 70, and both are in use.
        //
        // Every genre in this group has the same band, because they are counted in the same way. A
        // missing band would silently put a whole kind of music on the default window. A test checks
        // that each of these genres has a band.
        ["Hip-Hop / Rap"] = (70, 115),
        ["Rap"] = (70, 115),
        ["Old School Hip Hop"] = (70, 115),
        ["Trap"] = (70, 115),
        ["R&B"] = (70, 115),
        ["Soul"] = (70, 115),

        // The one-drop rhythm is on the half beat, which often misleads a beat reader.
        ["Reggae"] = (60, 105),

        // The bottom of this band is low on purpose: downtempo, dub and trip-hop are electronic
        // records at about 90 and are not miscounted house. The band is still narrow enough to catch
        // a 64 that should be 128, which is the error this genre usually has.
        ["Electronic / Dance"] = (90, 185),

        // These genres are made for the dance floor and are never downtempo, so their lower limit is
        // higher. A house record read at 62 is a house record counted at half its tempo.
        ["House"] = (100, 150),
        ["Techno"] = (105, 160),
        ["Club"] = (100, 160),

        // Four to the floor at a fixed pace. It is separate from house because a disco record is
        // really slower than a house record.
        ["Disco"] = (95, 135),

        // Played by a band on a beat, so it is counted in the same way.
        ["Funk"] = (85, 130),

        ["Rock"] = (80, 190),
        ["Indie"] = (80, 190),
        ["Pop"] = (80, 190),
        ["Punk"] = (80, 190),
        ["Metal"] = (80, 190),

        // Played by people at any pace. These bands are very wide on purpose: for these records the
        // second count is as good as the first.
        ["Jazz"] = (60, 200),
        ["Blues"] = (60, 200),
        ["Folk"] = (60, 200),

        ["Country"] = (60, 200),

        // A largo is really at 45, and doubling it would be wrong about the music.
        ["Classical"] = (40, 200),
        ["Neo-Classical"] = (40, 200),
        ["Opera"] = (40, 200),

        // A samba is written at 100 and danced at 200.
        ["Latin"] = (90, 210),
    };

    // Noise, Avant-Garde, Soundtrack, Video Game, Musical, Religious and Spoken Word are missing on
    // purpose. This table raises a question about a record that has a pulse, and a genre with no
    // opinion about how its records are counted should give no band. They use Default, which is
    // the widest band.

    /// <summary>The band for a record with no genre, or with a genre that has no entry in the
    /// list. With no context, the hint appears for readings outside the usual 70 to 180 window.</summary>
    public static readonly (double Low, double High) Default = (70, 180);

    /// <summary>The band in which a record of this genre is counted.</summary>
    public static (double Low, double High) BandFor(string? genre)
        => Genres.Match(genre) is { } known && Bands.TryGetValue(known, out var band) ? band : Default;

    /// <summary>
    /// The other way this record could be counted, or null when there is nothing to ask.
    ///
    /// <para>Null is the normal result. It is returned when the tempo is already inside the band for
    /// this kind of music, and also when neither the double nor the half is inside the band. In the
    /// second case the reading is odd in a way that an octave change will not fix, so a one-click
    /// correction should not be offered.</para>
    ///
    /// <para>At most one of the two can be the answer: a tempo below the band has a half that is
    /// lower still, and a tempo above the band has a double that is higher still.</para>
    /// </summary>
    public static double? OtherCount(double bpm, string? genre)
    {
        if (bpm <= 0)
            return null;

        var (low, high) = BandFor(genre);
        if (bpm >= low && bpm <= high)
            return null;

        if (bpm < low && bpm * 2 >= low && bpm * 2 <= high)
            return bpm * 2;

        if (bpm > high && bpm / 2 >= low && bpm / 2 <= high)
            return bpm / 2;

        return null;
    }
}

namespace RPM.Core.Analysis;

/// <summary>
/// A track's key: a tonic pitch class (0 = C, 1 = C#, … 11 = B) and a mode.
///
/// <para>It carries the Camelot wheel name because DJs mix with that form. On the wheel, neighbouring
/// numbers are a fifth apart and the two letters at one number are the relative major and minor. This
/// turns the question "do these two records clash?" into "are these labels adjacent?".
/// <see cref="Distance"/> measures exactly that adjacency.</para>
/// </summary>
public readonly record struct MusicalKey(int Tonic, bool IsMinor)
{
    /// <summary>No key was determined (silence, or nothing tonal enough to call).</summary>
    public static readonly MusicalKey Unknown = new(-1, false);

    public bool IsKnown => Tonic is >= 0 and < 12;

    private static readonly string[] Names =
        ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    /// <summary>The Camelot hour for each tonic: minor keys use A, major keys use B. Index by tonic.
    /// Minor: A minor = 8A, and each fifth up adds one hour. Major: C major = 8B.</summary>
    private static readonly int[] MinorHours = [5, 12, 7, 2, 9, 4, 11, 6, 1, 8, 3, 10];
    private static readonly int[] MajorHours = [8, 3, 10, 5, 12, 7, 2, 9, 4, 11, 6, 1];

    /// <summary>The hour hand on the Camelot wheel, 1..12, or 0 when the key is unknown.</summary>
    public int CamelotHour => !IsKnown ? 0 : IsMinor ? MinorHours[Tonic] : MajorHours[Tonic];

    /// <summary>The wheel label that a DJ reads, for example "8A" or "5B". Empty when the key is
    /// unknown.</summary>
    public string Camelot => IsKnown ? $"{CamelotHour}{(IsMinor ? 'A' : 'B')}" : "";

    /// <summary>"F# minor" / "C major". Empty when unknown.</summary>
    public string Name => IsKnown ? $"{Names[Tonic]} {(IsMinor ? "minor" : "major")}" : "";

    /// <summary>
    /// The form in which a key is written into a file's tags: "F#m", "C". Empty when unknown.
    ///
    /// <para>This is not <see cref="Name"/> and not <see cref="Camelot"/>. The ID3 TKEY frame is
    /// defined as at most three characters: A to G, 'b' and '#' for the half keys, and 'm' for minor.
    /// Other taggers and DJ programs read a key in this form. "A minor" is a longer text that no
    /// reader parses, and "8A" is a wheel position and not a key: written into TKEY it would be read
    /// as a track in the key of 8.</para>
    /// </summary>
    public string TagValue => IsKnown ? $"{Names[Tonic]}{(IsMinor ? "m" : "")}" : "";

    /// <summary>
    /// How far apart two keys are on the wheel, in "clash units": 0 = the same key, 1 = an easy move
    /// (one hour around the wheel, or the relative major/minor at the same hour), 2 = a move that
    /// works but can be heard, 3 or more = a clash. The result is <see cref="int.MaxValue"/> when
    /// either key is unknown, so callers can tell "far apart" from "not known".
    /// </summary>
    public int Distance(MusicalKey other)
    {
        if (!IsKnown || !other.IsKnown)
            return int.MaxValue;

        int hours = Math.Abs(CamelotHour - other.CamelotHour);
        hours = Math.Min(hours, 12 - hours);              // the wheel wraps
        bool modeSwap = IsMinor != other.IsMinor;

        // Same hour, other letter: the relative major or minor, which is the safest change of key.
        if (hours == 0)
            return modeSwap ? 1 : 0;

        // A mode swap AND an hour move is two changes at once. A good mix changes only one.
        return modeSwap ? hours + 1 : hours;
    }

    /// <summary>True when the two keys are one of the pairs that harmonic mixing uses: identical,
    /// the relative major or minor, or one hour either way.</summary>
    public bool MixesWith(MusicalKey other) => Distance(other) <= 1;

    public override string ToString() => IsKnown ? $"{Camelot} ({Name})" : "unknown key";

    /// <summary>Parses a Camelot label ("8A", "12B") back into a key. Unknown on anything else.
    /// This reads values back from the database, which may have been written by an older version.</summary>
    public static MusicalKey FromCamelot(string? label)
    {
        if (string.IsNullOrWhiteSpace(label) || label.Length < 2)
            return Unknown;

        char letter = char.ToUpperInvariant(label[^1]);
        if (letter is not ('A' or 'B'))
            return Unknown;
        if (!int.TryParse(label[..^1], out int hour) || hour is < 1 or > 12)
            return Unknown;

        bool minor = letter == 'A';
        var hours = minor ? MinorHours : MajorHours;
        int tonic = Array.IndexOf(hours, hour);
        return tonic < 0 ? Unknown : new MusicalKey(tonic, minor);
    }
}

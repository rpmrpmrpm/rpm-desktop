namespace RPM.Core.Analysis;

/// <summary>
/// What each of the key model's twenty-four outputs means.
///
/// <para>S-KEY numbers its keys in its own way: the twelve major keys running up from A, then the
/// twelve minor keys running up from B. Nothing else in the app counts like that. A
/// <see cref="MusicalKey"/> numbers its tonic from C, so the two numbering systems must be matched
/// somewhere, and this class does it.</para>
///
/// <para>The table is written out one key at a time and not computed from an offset, because of how
/// a mistake here fails. A table rotated by one gives every track a key, with a confidence, that is
/// wrong by a semitone. A table rotated by three names every track as its own relative minor.
/// Neither throws, neither looks wrong in the interface, and both make the Camelot scoring worse
/// than having no key at all. <c>KeyModelLabelsTests</c> checks the table against the model's own
/// list.</para>
/// </summary>
public static class KeyModelLabels
{
    /// <summary>The keys, in the order the model scores them.</summary>
    public static readonly IReadOnlyList<MusicalKey> Order =
    [
        // A major through G# major: the model's outputs 0 to 11.
        new(9, false), new(10, false), new(11, false), new(0, false),
        new(1, false), new(2, false), new(3, false), new(4, false),
        new(5, false), new(6, false), new(7, false), new(8, false),

        // B minor through Bb minor: outputs 12 to 23. The minor list starts two semitones above the
        // major one, so output n+12 is a tone above output n, not its relative minor (which would be
        // three semitones below). The second half does not line up with the first half.
        new(11, true), new(0, true), new(1, true), new(2, true),
        new(3, true), new(4, true), new(5, true), new(6, true),
        new(7, true), new(8, true), new(9, true), new(10, true),
    ];

    /// <summary>How many keys the model scores.</summary>
    public const int Count = 24;
}

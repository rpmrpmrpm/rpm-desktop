namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// Which records the room hangs a mirror ball for.
///
/// <para>One artist and a short list of genres: ABBA, and anything a tagger has called disco or funk. The
/// room behind Pegasus takes its colours from the sleeve and its light from the low end. This is the one
/// thing in it that is not read from the music but from who made it. That is a licence to be spent
/// carefully. A list that kept growing would make the room show the ball for half the library, and then the
/// ball would mean nothing when it did appear.</para>
///
/// <para><b>A whole word, never a substring.</b> The genre field is written by hand by whoever tagged the
/// file, in every variant there is: "Disco", "Italo Disco", "Nu-Disco", "Funk/Soul". So the text is cut
/// into words on its punctuation, and one of those words must be exactly one of the listed words. Matching
/// anywhere inside the string would be shorter, but it would hang a ball over every record filed under
/// Discovery, and on the artist side over every record by Abbath. The stricter rule means a genre written
/// as one word, such as "Eurodisco", is passed over. That is the right way round for a flourish: missing
/// one leaves a record that looks as it always did, and a false one makes the room misbehave.</para>
///
/// <para>Cutting on punctuation is also what makes the pairs a tagger writes work without being listed:
/// "Funk/Soul" is caught because one of its words is funk, and a record filed under soul alone is left
/// alone. That is the intended rule, not a side effect of the parsing.</para>
///
/// <para>A function of two tags and nothing else: no state, no library lookup, no service. That makes it
/// testable without a window, which is why it is here and not inside the player (the same approach that
/// <see cref="LyricHush"/> uses).</para>
/// </summary>
internal static class MirrorBall
{
    /// <summary>The one artist. Compared whole and without regard to case, so a pressing tagged "abba" is
    /// the same as one tagged "ABBA".</summary>
    internal const string Artist = "ABBA";

    /// <summary>The genres, on the same terms. This catches other artists' records without naming any of
    /// them. Each genre is a word a tagger writes alone or beside another, such as "Funk/Soul" or "Disco,
    /// Boogie", and either way one of these words decides.</summary>
    internal static readonly string[] Genres = ["Disco", "Funk"];

    /// <summary>Whether the room should hang a ball for the record now playing. Either tag on its own is
    /// enough: an ABBA track filed under pop is still ABBA, and a disco track by anybody at all is still
    /// disco.</summary>
    internal static bool HangsFor(string? artist, string? genre)
    {
        if (TagWord.Names(artist, Artist))
            return true;

        foreach (var word in Genres)
        {
            if (TagWord.Names(genre, word))
                return true;
        }

        return false;
    }
}

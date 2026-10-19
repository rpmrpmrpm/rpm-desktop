namespace RPM.Core.Metadata;

/// <summary>
/// What kind of music a record is: the shelf it goes on, and the corner of that shelf it comes from.
///
/// <para><b>One value rather than two strings, because the two are one answer.</b> A shelf on its own would
/// make every caller remember separately that a style exists, and then go and fetch it. That is easy to forget.
/// Returning a pair means the compiler asks the question at every call site. A caller that only wants the shelf
/// says <c>.Genre</c>, and it says so openly.</para>
///
/// <para><see cref="Subgenre"/> is null far more often than not. That is an ordinary answer, not a failure. Many
/// records are plain rock records and nothing narrower. What is never allowed is a style without a shelf. See
/// <see cref="None"/>. A style only makes sense as a corner of something.</para>
/// </summary>
/// <param name="Genre">One of <see cref="Genres.All"/>, or empty when the tag named no shelf at all.</param>
/// <param name="Subgenre">One of <see cref="Subgenres.All"/> under <paramref name="Genre"/>, or null when the
/// record only needs its shelf.</param>
public readonly record struct GenreChoice(string Genre, string? Subgenre)
{
    /// <summary>The tag named nothing the app knows. This is not a shelf. A record that lands here keeps its tag
    /// exactly as its writer left it, because a wrong shelf is worse than an unusual one.</summary>
    public static readonly GenreChoice None = new("", null);

    /// <summary>Whether a shelf was found. A style can never be present without one.</summary>
    public bool IsKnown => !string.IsNullOrEmpty(Genre);

    /// <summary>Whether the corner of the shelf is known too.</summary>
    public bool HasSubgenre => !string.IsNullOrWhiteSpace(Subgenre);

    /// <summary>The pair as one line, for a tooltip or a log: "House, Deep House", or just the shelf when there is
    /// no style. This is not translated. These are the names of kinds of music, the same as the shelves.</summary>
    public override string ToString()
        => !IsKnown ? "" : HasSubgenre ? $"{Genre}, {Subgenre}" : Genre;
}

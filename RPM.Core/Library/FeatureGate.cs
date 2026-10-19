namespace RPM.Core.Library;

/// <summary>What a shut gate is waiting for. It decides the sentence the locked surface prints, and
/// therefore what the listener is asked to go and do.</summary>
public enum GateNeed
{
    /// <summary>More records in the collection. The action is importing.</summary>
    Albums,

    /// <summary>More songs that answer this list's own question. The action is importing, because a band is
    /// drawn from what the collection holds.</summary>
    SongsInList,

    /// <summary>The measuring pass has not reached enough of the collection yet. There is no action. The
    /// listener waits, and the line says how far it has got.</summary>
    Measured,

    /// <summary>More listening. The action is playing something.</summary>
    Plays,
}

/// <summary>What a feature needs before it can say anything, and how far along the collection is.
///
/// <para><b>Why this is a type, not two ints at each call site.</b> Several surfaces ask the same question
/// about different features. The home screen asks about albums. A mood list asks about the songs in its
/// band. The same mood list asks about the measuring pass instead, when that is what holds it up. Written
/// out at each surface, the three would drift apart in their arithmetic (<see cref="ToGo"/> going negative
/// is the obvious case) and in which of them is asked.
/// </para>
///
/// <para><b>It does not know the threshold.</b> <see cref="Want"/> is passed in, because the numbers are
/// kept where they belong: <c>SmartPlaylists.LeastToShow</c> for the generated lists, and
/// <see cref="HomeGate.LeastAlbums"/> for the home screen. A gate that chose its own thresholds would be a
/// second opinion about them.</para>
/// </summary>
/// <param name="Need">What it is waiting for, which is what the locked surface says.</param>
/// <param name="Have">How much of it there is now.</param>
/// <param name="Want">How much there must be. Zero or less is a gate that is always open.</param>
public readonly record struct FeatureGate(GateNeed Need, int Have, int Want)
{
    /// <summary>Whether the feature may draw.</summary>
    public bool Open => Have >= Want;

    /// <summary>How much is still missing. It is never negative. The sentence reads "12 albums to go". A
    /// gate that is already open has nothing to go, not a negative amount.</summary>
    public int ToGo => Math.Max(0, Want - Have);

    /// <summary>How far along, from zero to one, for a progress bar.
    ///
    /// <para>Clamped at both ends. A gate that wants nothing reads as finished, so there is no division by
    /// zero. A gate past its threshold does not draw a bar that overflows its track.</para>
    /// </summary>
    public double Fraction => Want <= 0 ? 1.0 : Math.Clamp((double)Have / Want, 0.0, 1.0);
}

/// <summary>The home screen's gate.
/// </summary>
public static class HomeGate
{
    /// <summary>The fewest albums the home screen is worth drawing over.
    ///
    /// <para><b>Based on <see cref="HomeShelf.RowLength"/>, which is twenty.</b> With fewer than twenty
    /// albums, the "recently added" row is the whole collection in a different order. The suggestions row
    /// can only offer records already on the screen above it. <see cref="HomeShelf.Suggested"/> is seeded
    /// from five records (<see cref="HomeShelf.TasteSeeds"/>) and excludes both its seeds and the row
    /// above. On a shelf of fifteen it has nothing left to say. The listener would see one row of records
    /// repeated four times under four different headings.</para>
    ///
    /// <para>Twenty-five leaves a full row, plus enough albums outside the seeds that the rows differ from
    /// one another. That is the least that makes the screen mean what its headings say.</para>
    /// </summary>
    public const int LeastAlbums = 25;

    /// <summary>The home screen's gate, given what the shelf holds.</summary>
    public static FeatureGate For(int albums) => new(GateNeed.Albums, albums, LeastAlbums);
}

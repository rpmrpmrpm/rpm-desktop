namespace RPM.Desktop.Services.Art;

/// <summary>
/// Which decoded pictures a wall keeps for records nobody is looking at.
///
/// <para><b>Each wall states its own answer. It is not a default, because the two answers are truly
/// different.</b> The library window's wall has two tiers: a small picture that every record keeps, and
/// a full-size picture on top of what is on screen. Dropping the top tier is safe there. The tile falls
/// back to the picture underneath and only looks softer. The visualizer's wall holds one tier, at the
/// tile's own size, and nothing lies underneath it. A rule that drops pictures on a timer would drop
/// covers even when memory is short. Scroll away for three seconds and back, and every row you passed
/// has lost its cover. This type keeps the two rules apart.</para>
/// </summary>
/// <param name="Grace">How long a record keeps its picture after its row is reclaimed, whatever the
/// budget says. Use zero where the wall has no second tier to fall back on. Then nothing is dropped for
/// age, only under memory pressure. The repeater realises and reclaims a row at the edge of its window
/// again and again while its size estimate settles. This can happen about eleven times in one second,
/// so a grace of a second or two stops a row at that edge from costing anything.</param>
/// <param name="Budget">The most decoded cover the wall may hold off screen. It is in bytes, not
/// records, because a record's size varies. The tier follows the tile size, so one cover is about a
/// quarter of a megabyte at the small end of the size slider and about one and a half megabytes at the
/// large end. A cap in records would be about six times the memory at one end of the same setting.</param>
/// <param name="DropsResident">Whether the resident layer is dropped too. False where it is the picture
/// the tile falls back to. True where it is the picture itself, and the encoded bytes underneath are the
/// fallback.</param>
/// <param name="DecodedCollection">How much of the collection is held DECODED before it is looked at.
/// Zero decodes only what is on screen. A larger value means a tile paints on the frame it is realised,
/// not a frame or two later. This is what makes a fast scroll down a long wall look smooth. The cost is
/// pixels for records nobody has reached yet. Only the window's wall uses it, because only that wall has
/// a tier small enough to hold for a whole collection.</param>
public sealed record CoverHold(
    TimeSpan Grace, long Budget, bool DropsResident, long DecodedCollection)
{
    /// <summary>The library window's wall. A sharp layer sits over a resident one. The sharp layer is
    /// given back on a timer and has a small budget. The resident tier is held decoded for the whole
    /// collection.</summary>
    public static CoverHold SharpOverResident { get; } = new(
        Grace: TimeSpan.FromSeconds(3),
        Budget: 48L * 1024 * 1024,
        DropsResident: false,
        DecodedCollection: 320L * 1024 * 1024);

    /// <summary>The visualizer's wall. One picture at the tile's own size. It is dropped only under memory
    /// pressure, never on a timer, and it is never held ahead of the screen.</summary>
    public static CoverHold OnePicture { get; } = new(
        Grace: TimeSpan.Zero,
        Budget: 320L * 1024 * 1024,
        DropsResident: true,
        DecodedCollection: 0);

    /// <summary>
    /// Which off-screen records give their pictures back now.
    ///
    /// <para>Newest first, so the pictures that survive are the ones that left the screen most recently.
    /// These are the row just above or below the screen, which is likely to be asked for again. A record
    /// past its grace period is dropped whatever the budget says. Once the budget is used up, every other
    /// record is dropped too.</para>
    /// </summary>
    /// <param name="offScreen">Each off-screen record: the record itself, the clock reading when its row
    /// was reclaimed, and the edge its picture was decoded at.</param>
    /// <param name="now">The same clock, read now.</param>
    public List<T> ToDrop<T>(IEnumerable<(T Card, long Since, int Edge)> offScreen, long now)
    {
        var drop = new List<T>();
        var held = 0L;
        foreach (var entry in offScreen.OrderByDescending(e => e.Since))
        {
            if (Grace > TimeSpan.Zero && now - entry.Since >= Grace.TotalMilliseconds)
            {
                drop.Add(entry.Card);
                continue;
            }

            held += ArtScaler.BytesFor(entry.Edge);
            if (held > Budget)
                drop.Add(entry.Card);
        }
        return drop;
    }
}

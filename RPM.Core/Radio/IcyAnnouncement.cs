using RPM.Fingerprint.Catalog;
namespace RPM.Core.Radio;

/// <param name="Artist">Who is playing, or null where the announcement does not name one.</param>
/// <param name="Title">What is playing, or, where <paramref name="IsSong"/> is false, whatever the station
/// said instead.</param>
/// <param name="IsSong">False where the announcement is not a record at all: the station's own name, its
/// programme, a jingle. The caller should show it as one line rather than as a pair.</param>
/// <param name="Reversed">True where the halves were swapped from the order the station wrote them in. It
/// is carried so that <see cref="IcyOrders"/> can put them back before judging them. What IcyOrders learns
/// is about the station's own order, and a pair that is already turned round would teach it the
/// opposite.</param>
public readonly record struct Announced(string? Artist, string? Title, bool IsSong, bool Reversed = false);

/// <summary>
/// What a station is announcing, read as carefully as one string allows.
///
/// <para><b>ICY carries one field and no rules.</b> <see cref="IcyTitle"/> splits it by the usual
/// convention, <c>Artist - Title</c>. That is right for most stations and wrong in two ways that are worth
/// knowing. Both were seen on Dutch stations:</para>
///
/// <list type="bullet">
/// <item><b>The pair is the other way round.</b> Qmusic announces <c>Man! I Feel Like A Woman - SHANIA
/// TWAIN</c>. Read by the usual convention, that is a band called Man! I Feel Like A Woman, and the banner
/// prints the song where the artist goes.</item>
/// <item><b>It is not a record.</b> NPO Radio 2 announces <c>NPO Radio 2 - RINKELDEKINKEL - BNNVARA</c>:
/// the station itself, the programme and the broadcaster. Read by the usual convention, that is an artist
/// called NPO Radio 2.</item>
/// </list>
/// </summary>
public static class IcyAnnouncement
{
    /// <summary>
    /// Reads one announcement.
    /// </summary>
    /// <param name="announced">The ICY string.</param>
    /// <param name="station">The station's own name. This is how the second case above is caught. Nothing
    /// else can catch it: "NPO Radio 2" is a perfectly good artist name to anything that does not know
    /// which station is playing.</param>
    public static Announced Read(string? announced, string? station)
    {
        var (artist, title) = IcyTitle.Split(announced);

        if (title is null)
            return new Announced(null, null, IsSong: false);

        // Not a record: the station is talking about itself. The test is its own name in the artist
        // place. It is a certain test, because a station does not play a band named after itself.
        if (artist is not null && NamesTheStation(artist, station))
            return new Announced(null, announced?.Trim(), IsSong: false);

        if (artist is null)
            return new Announced(null, title, IsSong: true);

        // The pair may be the other way round. This is done only where the announcement says so
        // plainly, see Shouted. It is the last resort: IcyOrders first checks the order it has learnt
        // for this station, and uses this guess only for a station it has not yet learnt.
        return Shouted(title) && !Shouted(artist)
            ? new Announced(title, artist, IsSong: true, Reversed: true)
            : new Announced(artist, title, IsSong: true);
    }

    /// <summary>Whether this half of an announcement is the station's own name. This is shared with
    /// RadioStreamSource, which treats an announcement that is only this name as no title at all (see
    /// RadioStreamSource.OnTitle).
    /// </summary>
    internal static bool NamesTheStation(string half, string? station)
    {
        if (string.IsNullOrWhiteSpace(station))
            return false;

        // Compared the same way as every other name in the app. Also compared with the printed station
        // name, so a station whose directory entry has a strapline is still recognised in its own
        // announcements. See RadioNames.
        return NameMatch.Same(half, station)
               || NameMatch.Same(half, RadioNames.Clean(station));
    }

    /// <summary>
    /// Whether a half is written in capitals, which is how the stations that reverse the pair mark the
    /// artist. Qmusic: "Man! I Feel Like A Woman - SHANIA TWAIN".
    /// </summary>
    /// <remarks>
    /// <para><b>A hint, used only where it is clear.</b> It decides nothing unless exactly one half is in
    /// capitals. A station that writes the whole line in capitals gives no hint, and is read by the usual
    /// convention. So is a station that uses no capitals.</para>
    /// <para>A half counts as shouted only when it has no lowercase letters and at least <see
    /// cref="Enough"/> capitals. So "R.E.M. - LOSING MY RELIGION" is left alone: "R.E.M." has too few
    /// letters to count as shouted.</para>
    /// </remarks>
    private static bool Shouted(string half)
    {
        var letters = 0;
        foreach (var c in half)
        {
            if (!char.IsLetter(c))
                continue;
            if (char.IsLower(c))
                return false;
            letters++;
        }

        // A short run of capitals is not a shout. Names such as "U2" or initialisms such as "BBC" are
        // too short to count.
        return letters >= Enough;
    }

    /// <summary>How many capitals a half needs before it counts as shouted rather than as an
    /// initialism.</summary>
    private const int Enough = 4;
}

namespace RPM.Core.Radio;

/// <summary>
/// Which way round a station gives its pairs, learned from what it has actually said.
///
/// <para><b>Why learning is the only way.</b> ICY carries one string and no rules. Most stations write
/// <c>Artist - Title</c>. Qmusic writes <c>Man! I Feel Like A Woman - SHANIA TWAIN</c>, which read by the
/// usual convention is a band called Man! I Feel Like A Woman. No single announcement settles it, because
/// both halves are just words. So the question is answered by asking which half is a musician, using
/// something that knows about music, and by remembering the answer for each station.</para>
///
/// <para><b>The listener's own library is what knows.</b> The <see cref="IcyOrders(Func{string, bool})"/>
/// constructor is given the test "is this a name my library files records under". It is exact when it
/// returns true, and silent when it does not. This is the right shape. The listener's own records are the
/// one source that needs no network, no rate limit and no guessing.</para>
///
/// <para><b>It is then remembered, and this is what makes it work at all.</b> A station's order does not
/// change between songs. Once the votes show a lead of two, the station is settled. A vote one way cancels
/// a vote the other way, so a real majority is needed. From then on every announcement from that station is
/// read that way, including the many songs the library has never heard of. One recognised record teaches
/// the station.</para>
/// </summary>
public sealed class IcyOrders
{
    private readonly Func<string, bool> _known;
    private readonly Dictionary<string, Tally> _stations = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>
    /// What a source is given when nobody has said which library to ask. This is the reading the app used
    /// before any library was consulted: the capitals guess, and then the usual convention.
    ///
    /// <para>It is a named value rather than a null. A source's order is then always a stated answer, and
    /// the fallback has a name that can be described. It is a real reading, not a broken one, which is why
    /// it is allowed to be a default at all.</para>
    /// </summary>
    public static IcyOrders Unlearned { get; } = new(_ => false);

    /// <param name="knownArtist">Whether a name is one the library files records under. Anyone who builds
    /// their own must pass this. Without it, the result is the capitals guess with a dictionary attached,
    /// and a caller who meant to pass one would get that without any warning. See <see cref="Unlearned"/>
    /// for the case where there is deliberately nothing to ask.</param>
    public IcyOrders(Func<string, bool> knownArtist)
    {
        ArgumentNullException.ThrowIfNull(knownArtist);
        _known = knownArtist;
    }

    /// <summary>
    /// Reads one announcement, and learns from it.
    /// </summary>
    /// <param name="station">The station's id, which is what the order is remembered against. A station's
    /// own name is not used as the key. Two stations often share one name, and the id never
    /// changes.</param>
    /// <param name="announced">What it said.</param>
    /// <param name="stationName">Its name, for catching the announcements that are not records at all. See
    /// <see cref="IcyAnnouncement"/>.</param>
    public Announced Read(string? station, string? announced, string? stationName)
    {
        // Everything that is not a plain pair (a station naming itself, a bare title, nothing at all) is
        // settled before order is considered, and it teaches nothing.
        var heard = IcyAnnouncement.Read(announced, stationName);
        if (!heard.IsSong || heard.Artist is null || heard.Title is null)
            return heard;

        // IcyAnnouncement may already have swapped the pair because of the capitals hint. What follows is
        // about the halves as the station wrote them, so they are swapped back first.
        var (head, tail) = heard.Reversed ? (heard.Title, heard.Artist) : (heard.Artist, heard.Title);

        var vote = Recognised(head, tail);
        var settled = Remember(station, vote);

        return settled switch
        {
            // The station is settled, so its order is used whatever this one announcement looks like.
            Order.Reversed => new Announced(tail, head, IsSong: true, Reversed: true),
            Order.Convention => new Announced(head, tail, IsSong: true, Reversed: false),

            // The station is not settled yet. Use this announcement's own evidence, and the capitals hint
            // where there is none.
            _ => vote switch
            {
                Order.Reversed => new Announced(tail, head, IsSong: true, Reversed: true),
                Order.Convention => new Announced(head, tail, IsSong: true, Reversed: false),
                _ => heard,
            },
        };
    }

    /// <summary>
    /// What the library says about these two halves: the one it knows as an artist is the artist.
    /// </summary>
    /// <remarks>
    /// Both recognised is no evidence, not a tie to be broken. "Madonna - Music" and "Music - Madonna" are
    /// the same two words, and a library full of records could recognise either. Neither recognised is no
    /// evidence either. That is most announcements on a station that plays music nobody owns.
    /// </remarks>
    private Order Recognised(string head, string tail)
    {
        bool first, second;
        try
        {
            first = _known(head);
            second = _known(tail);
        }
        catch
        {
            return Order.Unknown;   // the library is busy or closing, so it gives no answer
        }

        return (first, second) switch
        {
            (true, false) => Order.Convention,
            (false, true) => Order.Reversed,
            _ => Order.Unknown,
        };
    }

    /// <summary>Adds a vote and answers what the station has settled on, if anything.</summary>
    private Order Remember(string? station, Order vote)
    {
        if (string.IsNullOrWhiteSpace(station))
            return Order.Unknown;   // nothing to remember it against

        lock (_gate)
        {
            var tally = _stations.TryGetValue(station, out var kept) ? kept : default;

            if (vote == Order.Convention) tally = tally with { Convention = tally.Convention + 1 };
            else if (vote == Order.Reversed) tally = tally with { Reversed = tally.Reversed + 1 };

            if (vote != Order.Unknown)
                _stations[station] = tally;

            var lead = tally.Convention - tally.Reversed;
            return lead >= Settled ? Order.Convention
                 : lead <= -Settled ? Order.Reversed
                 : Order.Unknown;
        }
    }

    /// <summary>How far ahead one reading has to be before the station is taken as settled. It is two, so
    /// that one odd announcement cannot decide it. A compilation credited to a title, or a guest billed
    /// first, is an example. A station that really does reverse its pairs still settles within a couple of
    /// songs.</summary>
    private const int Settled = 2;

    private readonly record struct Tally(int Convention, int Reversed);

    private enum Order
    {
        Unknown,
        Convention,
        Reversed,
    }
}

using RPM.Core.Radio;

namespace RPM.Core.Tests;

public class IcyOrdersTests
{
    /// <summary>A collection that has albums by these artists and no others.</summary>
    private static IcyOrders Owning(params string[] artists)
    {
        var known = new HashSet<string>(artists, StringComparer.OrdinalIgnoreCase);
        return new IcyOrders(name => known.Contains(name.Trim()));
    }

    /// <summary>An empty collection: the listener owns no music at all.</summary>
    private static IcyOrders Owning() => new(_ => false);

    private const string Q = "qmusic-uuid";

    // ── what the collection decides ──

    /// <summary>Qmusic's real title. The library has Shania Twain and has no band called "Man! I Feel
    /// Like A Woman". That is the only evidence used.</summary>
    [Fact]
    public void The_collection_settles_which_half_is_the_musician()
    {
        var orders = Owning("SHANIA TWAIN");

        var heard = orders.Read(Q, "Man! I Feel Like A Woman - SHANIA TWAIN", "Qmusic");

        Assert.Equal("SHANIA TWAIN", heard.Artist);
        Assert.Equal("Man! I Feel Like A Woman", heard.Title);
    }

    [Fact]
    public void An_ordinary_station_is_settled_the_ordinary_way()
    {
        var orders = Owning("Fleetwood Mac");

        var heard = orders.Read("sky", "Fleetwood Mac - Dreams", "Sky Radio");

        Assert.Equal("Fleetwood Mac", heard.Artist);
        Assert.Equal("Dreams", heard.Title);
    }

    // ── the order is then remembered per station ──

    /// <summary>Recognised songs teach the station's order. Later songs are read in that order even
    /// when the collection does not know any of them, which is true of most songs a station plays.
    /// </summary>
    [Fact]
    public void A_station_once_learnt_reads_the_records_nobody_owns()
    {
        var orders = Owning("SHANIA TWAIN", "ABBA");

        // Two confirmed announcements settle it.
        orders.Read(Q, "Man! I Feel Like A Woman - SHANIA TWAIN", "Qmusic");
        orders.Read(Q, "Waterloo - ABBA", "Qmusic");

        // Then one it cannot confirm, with no capitals signal to use either.
        var heard = orders.Read(Q, "Ik Neem Je Mee - Gers Pardoel", "Qmusic");

        Assert.Equal("Gers Pardoel", heard.Artist);
        Assert.Equal("Ik Neem Je Mee", heard.Title);
    }

    /// <summary>The same in the normal order: a station known to use "artist - title" is not swapped
    /// by a title in capitals.</summary>
    [Fact]
    public void A_learnt_station_is_not_undone_by_a_shouted_title()
    {
        var orders = Owning("Fleetwood Mac", "Bill Withers");

        orders.Read("sky", "Fleetwood Mac - Dreams", "Sky Radio");
        orders.Read("sky", "Bill Withers - Lovely Day", "Sky Radio");

        // The case the capitals guess gets wrong on its own (see IcyAnnouncementTests).
        var heard = orders.Read("sky", "Bill Withers - AIN'T NO SUNSHINE", "Sky Radio");

        Assert.Equal("Bill Withers", heard.Artist);
        Assert.Equal("AIN'T NO SUNSHINE", heard.Title);
    }

    /// <summary>What is learnt applies only to that station. Another station starts with no
    /// history.</summary>
    [Fact]
    public void What_one_station_taught_is_not_applied_to_another()
    {
        var orders = Owning("SHANIA TWAIN", "ABBA");

        orders.Read(Q, "Man! I Feel Like A Woman - SHANIA TWAIN", "Qmusic");
        orders.Read(Q, "Waterloo - ABBA", "Qmusic");

        var heard = orders.Read("sky", "Some Band - Some Song", "Sky Radio");

        Assert.Equal("Some Band", heard.Artist);
        Assert.Equal("Some Song", heard.Title);
    }

    /// <summary>It needs a real majority: one unusual title (a guest named first, a compilation
    /// credited to its title) must not decide a station.</summary>
    [Fact]
    public void One_announcement_the_other_way_does_not_settle_a_station()
    {
        var orders = Owning("ABBA", "Fleetwood Mac");

        orders.Read("mixed", "Waterloo - ABBA", "Some Station");          // says reversed
        orders.Read("mixed", "Fleetwood Mac - Dreams", "Some Station");   // says normal order

        // Nothing decided, and no evidence in this title: the normal order is used.
        var heard = orders.Read("mixed", "Unknown Band - Unknown Song", "Some Station");

        Assert.Equal("Unknown Band", heard.Artist);
        Assert.Equal("Unknown Song", heard.Title);
    }

    // ── what it does not decide ──

    /// <summary>When both parts are recognised there is no evidence, not a tie: "Madonna - Music"
    /// and "Music - Madonna" are the same two words.</summary>
    [Fact]
    public void Recognising_both_halves_teaches_nothing()
    {
        var orders = Owning("Madonna", "Music");

        orders.Read("both", "Madonna - Music", "Some Station");
        orders.Read("both", "Madonna - Music", "Some Station");

        var heard = orders.Read("both", "Unknown Band - Unknown Song", "Some Station");

        Assert.Equal("Unknown Band", heard.Artist);
    }

    /// <summary>A listener who owns nothing gets the fallback: the capitals guess, and the normal
    /// order where there is no capitals signal.</summary>
    [Fact]
    public void An_empty_collection_falls_back_to_the_guess()
    {
        var orders = Owning();

        var shouted = orders.Read(Q, "Man! I Feel Like A Woman - SHANIA TWAIN", "Qmusic");
        Assert.Equal("SHANIA TWAIN", shouted.Artist);

        var plain = orders.Read("sky", "Fleetwood Mac - Dreams", "Sky Radio");
        Assert.Equal("Fleetwood Mac", plain.Artist);
    }

    /// <summary>A station naming itself is detected before the order is considered, and teaches
    /// nothing. Otherwise every programme title would count as a vote.</summary>
    [Fact]
    public void A_station_naming_itself_is_still_not_a_record()
    {
        var orders = Owning("BNNVARA");

        var heard = orders.Read("npo2", "NPO Radio 2 - RINKELDEKINKEL - BNNVARA", "NPO Radio 2");

        Assert.False(heard.IsSong);
        Assert.Null(heard.Artist);

        // It left no effect: the next title is read in the normal order.
        var after = orders.Read("npo2", "Some Band - Some Song", "NPO Radio 2");
        Assert.Equal("Some Band", after.Artist);
    }

    [Fact]
    public void Nothing_announced_is_nothing()
    {
        var orders = Owning("ABBA");

        Assert.False(orders.Read(Q, null, "Qmusic").IsSong);
        Assert.False(orders.Read(Q, "   ", "Qmusic").IsSong);
    }

    /// <summary>A station with no id has nothing to remember against, so each title is read on its
    /// own, with no exception.</summary>
    [Fact]
    public void A_station_with_no_id_is_still_read()
    {
        var orders = Owning("ABBA");

        var heard = orders.Read(null, "Waterloo - ABBA", "Some Station");

        Assert.Equal("ABBA", heard.Artist);
    }

    /// <summary>The collection can be in the middle of a rebuild or closing. Then it does not answer,
    /// and the reading uses the fallback instead of throwing.</summary>
    [Fact]
    public void A_collection_that_cannot_answer_is_not_an_error()
    {
        var orders = new IcyOrders(_ => throw new InvalidOperationException("mirror is busy"));

        var heard = orders.Read(Q, "Fleetwood Mac - Dreams", "Sky Radio");

        Assert.Equal("Fleetwood Mac", heard.Artist);
    }
}

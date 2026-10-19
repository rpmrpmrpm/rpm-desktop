using RPM.Core.Metadata.Providers;

namespace RPM.Core.Tests;

/// <summary>
/// What a band member played, out of MusicBrainz's attribute list.
///
/// <para>Every case below except the guards is taken from the live catalogue rather than imagined:
/// Dire Straits, the nine members the app actually draws. Both faults this file exists to prevent are
/// in that one band: an attribute list that leads with "additional" rather than the instrument, and a
/// drummer the catalogue calls a membranophone.</para>
/// </summary>
public class BandRolesTests
{
    // ── the real thing ──

    /// <summary>The membership qualifier is not a role, and it can come FIRST, which is what makes
    /// "take the first attribute" wrong rather than merely rough. Chris White plays saxophone.</summary>
    [Fact]
    public void A_word_about_the_membership_is_not_a_role()
    {
        Assert.Equal("saxophone", BandRoles.Spoken(["additional", "saxophone"]));
        Assert.Equal("guitar", BandRoles.Spoken(["guitar", "original"]));
        Assert.Equal("bass guitar", BandRoles.Spoken(["bass guitar", "original"]));
    }

    /// <summary>The catalogue files instruments under the Hornbostel-Sachs families, so a drummer is a
    /// membranophone. Correct, and not a word to put under a record sleeve.</summary>
    [Theory]
    [InlineData("membranophone", "drums")]
    [InlineData("idiophone", "percussion")]
    [InlineData("aerophone", "wind")]
    [InlineData("chordophone", "strings")]
    [InlineData("electrophone", "electronics")]
    public void The_taxonomy_is_turned_back_into_english(string attribute, string spoken)
    {
        Assert.Equal(spoken, BandRoles.Spoken([attribute]));
    }

    [Fact]
    public void Pick_Withers_is_a_drummer()
    {
        Assert.Equal("drums", BandRoles.Spoken(["membranophone", "original"]));
    }

    /// <summary>Two, because one would print "guitar" for a man who sang every word of it.</summary>
    [Fact]
    public void Mark_Knopfler_plays_guitar_and_sings()
    {
        Assert.Equal("guitar, lead vocals", BandRoles.Spoken(["guitar", "lead vocals", "original"]));
    }

    /// <summary>And it stops at two, so one session player's long list of instruments does not push
    /// the name off the line it shares.</summary>
    [Fact]
    public void A_long_list_stops_at_two()
    {
        var spoken = BandRoles.Spoken(["guitar", "keyboard", "saxophone", "drums", "vocals"]);

        Assert.Equal("guitar, keyboard", spoken);
        Assert.Equal(BandRoles.MostRoles, spoken!.Split(',').Length);
    }

    /// <summary>A drummer credited both ways is one drummer. The families fold onto words the list may
    /// already carry, so the duplicate can only appear after the mapping.</summary>
    [Fact]
    public void One_instrument_named_twice_is_said_once()
    {
        Assert.Equal("drums", BandRoles.Spoken(["membranophone", "drums", "original"]));
        Assert.Equal("guitar", BandRoles.Spoken(["guitar", "Guitar"]));
    }

    // ── nothing to say ──

    /// <summary>Null rather than an empty string, and the surfaces draw nothing for it. A line with no
    /// role is better than a line with a birthdate on it.
    /// </summary>
    [Fact]
    public void A_membership_with_no_instrument_on_it_says_nothing()
    {
        Assert.Null(BandRoles.Spoken(["original"]));
        Assert.Null(BandRoles.Spoken(["original", "founder", "guest"]));
        Assert.Null(BandRoles.Spoken([]));
        Assert.Null(BandRoles.Spoken(null));
    }

    [Fact]
    public void Blank_and_padded_attributes_are_not_roles()
    {
        Assert.Null(BandRoles.Spoken(["", "   "]));
        Assert.Equal("guitar", BandRoles.Spoken(["  guitar  ", ""]));
    }

    /// <summary>Case is the catalogue's business, not this app's: "Original" is the same qualifier as
    /// "original" and must not survive as a role because somebody typed it differently.</summary>
    [Fact]
    public void The_qualifiers_are_refused_whatever_the_case()
    {
        Assert.Null(BandRoles.Spoken(["Original"]));
        Assert.Equal("drums", BandRoles.Spoken(["ADDITIONAL", "Membranophone"]));
    }
}

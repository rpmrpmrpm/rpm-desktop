using RPM.Core.Radio;

namespace RPM.Core.Tests;

/// <summary>The dial's call signs: what a station is marked by when there is room for four letters
/// and no more.</summary>
public class StationSignTests
{
    [Theory]
    // A name that is already a mark keeps it, whatever noise the directory wrapped it in.
    [InlineData("KEXP", "KEXP")]
    [InlineData("Radio Nova", "NOVA")]
    [InlineData("Jazz FM", "JAZZ")]
    [InlineData("NPO Radio 2", "NPO2")]
    [InlineData("BBC Radio 1", "BBC1")]
    // A number IS the name for plenty of stations, and it survives the same way letters do.
    [InlineData("Radio 538", "538")]
    [InlineData("100% NL", "100")]
    // Three carrying words or more: initials, capped at four.
    [InlineData("Smooth Jazz Lounge Radio", "SJL")]
    [InlineData("Kink Distortion Amsterdam Online", "KDA")]
    [InlineData("Sky Radio Christmas Hits Nonstop", "SCHN")]
    // Joining words are not initials: a listener does not say them either.
    [InlineData("Voice of the People Network", "VPN")]
    // One or two words of substance: the opening letters, not a two-letter stub.
    [InlineData("Classical Music", "CLAS")]
    [InlineData("Klassiek", "KLAS")]
    // Accents fold, so a station signs the same however its name was typed.
    [InlineData("Rádio Comercial", "COME")]
    // Punctuation the station put in its own name ends a word rather than joining one.
    [InlineData("Q-Music", "QMUS")]
    public void Signs(string name, string expected) => Assert.Equal(expected, StationSign.For(name));

    [Fact]
    public void Nameless_station_gets_no_sign()
    {
        Assert.Equal("", StationSign.For(null));
        Assert.Equal("", StationSign.For("   "));
        Assert.Equal("", StationSign.For("!!! ---"));
    }

    [Fact]
    public void A_name_that_is_only_noise_still_signs()
    {
        // "Radio" is dropped because it says nothing about WHICH station, unless it was the whole
        // name, in which case a mark that says "radio" beats no mark at all.
        Assert.Equal("RADI", StationSign.For("Radio"));
        Assert.Equal("FM", StationSign.For("FM"));
    }

    [Fact]
    public void No_sign_is_ever_wider_than_the_card()
    {
        string[] names =
        [
            "Smooth Jazz Lounge Radio — The Netherlands",
            "Хит FM",
            "1.FM - Absolute Top 40 (Hit Radio)",
            "WWWWWWWWWWWWWWWWWWWW",
            "A B C D E F G H",
        ];
        foreach (var name in names)
            Assert.InRange(StationSign.For(name).Length, 1, 4);
    }

    [Fact]
    public void The_same_name_always_signs_the_same()
    {
        // The card and the row derive the mark separately; they can only agree because this is pure.
        Assert.Equal(StationSign.For("Radio Paradise"), StationSign.For("  radio paradise  "));
    }
}

using RPM.Core.Metadata.Providers;

namespace RPM.Core.Tests;

/// <summary>
/// A band's line-up as it stands: who has left is taken off, where Wikidata says so, and each member
/// carries their photograph.
/// </summary>
public class BandLineupTests
{
    private static string Claim(string id, string? endYear = null)
    {
        var value = "\"mainsnak\": { \"datavalue\": { \"value\": { \"id\": \"" + id + "\" } } }";
        return endYear is null
            ? "{ " + value + " }"
            : "{ " + value + ", \"qualifiers\": { \"P582\": [ { \"datavalue\": { \"value\": { \"time\": \"+"
              + endYear + "-01-01T00:00:00Z\" } } } ] } }";
    }

    /// <summary>A band, Q1, as its entity and its members' entities answer: Q10 is in it; Q11 left in
    /// 2001 by the band's own list; Q12 left in 1990 and came back; Q13 left by their own list of bands;
    /// Q14 nobody wrote anything about.</summary>
    private static string Answer(string? dissolved = null) => $$"""
    {
      "entities": {
        "Q1": { "claims": {
          "P527": [ {{Claim("Q10")}}, {{Claim("Q11", "2001")}}, {{Claim("Q12", "1990")}}, {{Claim("Q12")}} ]
          {{(dissolved is null ? "" : $$""", "P576": [ { "mainsnak": { "datavalue": { "value": { "time": "+{{dissolved}}-06-01T00:00:00Z" } } } } ]""")}}
        } },
        "Q10": { "claims": { "P18": [ { "mainsnak": { "datavalue": { "value": "Carol Decker.jpg" } } } ] } },
        "Q11": { "claims": {} },
        "Q12": { "claims": {} },
        "Q13": { "claims": { "P463": [ {{Claim("Q1", "1999")}}, {{Claim("Q99")}} ] } },
        "Q14": { "claims": {} }
      }
    }
    """;

    private static readonly BandMember[] Members =
    [
        new("Carol Decker", "Q10"), new("Gone 2001", "Q11"), new("Came Back", "Q12"),
        new("Left Themselves", "Q13"), new("Nobody Said", "Q14"), new("Only An Mbid", "6c1f0c9e-0000-0000-0000-000000000000"),
    ];

    [Fact]
    public void Who_left_is_taken_off_and_everybody_else_stays()
    {
        var current = EncyclopediaLookup.CurrentLineup(Answer(), "Q1", Members);

        Assert.Equal(["Carol Decker", "Came Back", "Nobody Said", "Only An Mbid"], current.Select(m => m.Name));
    }

    [Fact]
    public void A_member_carries_their_photograph()
    {
        var carol = EncyclopediaLookup.CurrentLineup(Answer(), "Q1", Members)[0];

        Assert.Equal("https://commons.wikimedia.org/wiki/Special:FilePath/Carol%20Decker.jpg?width=240", carol.Photo);
    }

    /// <summary>The papers' MusicBrainz path names members by their MusicBrainz ids. They are found by the
    /// entity that files the id (P434), and get their photographs even where the band has no entity, in
    /// which case nobody is taken off.</summary>
    [Fact]
    public void A_member_named_by_a_musicbrainz_id_gets_their_photograph()
    {
        const string body = """
        {
          "entities": {
            "Q4028": { "claims": {
              "P434": [ { "mainsnak": { "datavalue": { "value": "05517043-ff78-4988-9c22-88c68588ebb9" } } } ],
              "P18": [ { "mainsnak": { "datavalue": { "value": "Paul Simon.jpg" } } } ]
            } }
          }
        }
        """;
        BandMember[] duo = [new("Paul Simon", "05517043-ff78-4988-9c22-88c68588ebb9"), new("Art Garfunkel", "fc0a5289-4b77-4246-9c8d-857c8b617f5d")];

        var current = EncyclopediaLookup.CurrentLineup(body, band: null, duo);

        Assert.Equal(2, current.Count);
        Assert.Equal("https://commons.wikimedia.org/wiki/Special:FilePath/Paul%20Simon.jpg?width=240", current[0].Photo);
        Assert.Null(current[1].Photo);
    }

    /// <summary>A band that split up keeps its last line-up: memberships that ended when the band did are
    /// the band's ending, not somebody leaving.</summary>
    [Fact]
    public void A_band_that_split_keeps_its_last_line_up()
    {
        var current = EncyclopediaLookup.CurrentLineup(Answer(dissolved: "2001"), "Q1", Members);

        Assert.Contains(current, m => m.Name == "Gone 2001");
        Assert.DoesNotContain(current, m => m.Name == "Left Themselves");
    }
}

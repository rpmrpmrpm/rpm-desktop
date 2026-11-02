using RPM.Core.Metadata.Providers;

namespace RPM.Core.Tests;

/// <summary>
/// A band's line-up as it stands: who has left is taken off, where Wikidata says so, and each member
/// carries their photograph.
/// </summary>
public class BandLineupTests
{
    private static string Claim(string id, string? endYear = null, string? startYear = null)
    {
        var value = "\"mainsnak\": { \"datavalue\": { \"value\": { \"id\": \"" + id + "\" } } }";
        var qualifiers = new List<string>();
        if (startYear is not null)
            qualifiers.Add("\"P580\": [ " + Time(startYear) + " ]");
        if (endYear is not null)
            qualifiers.Add("\"P582\": [ " + Time(endYear) + " ]");
        return qualifiers.Count == 0
            ? "{ " + value + " }"
            : "{ " + value + ", \"qualifiers\": { " + string.Join(", ", qualifiers) + " } }";
    }

    private static string Time(string year)
        => "{ \"datavalue\": { \"value\": { \"time\": \"+" + year + "-01-01T00:00:00Z\" } } }";

    /// <summary>A band, Q1, as its entity and its members' entities answer: Q10 is in it; Q11 left in
    /// 2001 by the band's own list; Q12 left in 1990 and came back in 1995; Q13 left by their own list of
    /// bands; Q14 nobody wrote anything about.</summary>
    private static string Answer(string? dissolved = null) => $$"""
    {
      "entities": {
        "Q1": { "claims": {
          "P527": [ {{Claim("Q10")}}, {{Claim("Q11", "2001")}}, {{Claim("Q12", "1990")}}, {{Claim("Q12", startYear: "1995")}} ]
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

    /// <summary>The Beatles as Wikidata has them, cut down. The band's list ends the four in 1970, the year
    /// it split; John Lennon's own list ends his membership in 1969. Chas Newby and Tommy Moore left early,
    /// and each has an open membership with no start as well.</summary>
    private const string Beatles = """
    {
      "entities": {
        "Q1299": { "claims": {
          "P576": [ { "mainsnak": { "datavalue": { "value": { "time": "+1970-04-10T00:00:00Z" } } } } ],
          "P527": [ BAND ]
        } },
        "Q1203": { "claims": { "P463": [ LENNON ] } },
        "Q2599": { "claims": {} },
        "Q207335": { "claims": {} },
        "Q5087041": { "claims": { "P463": [ NEWBY ] } },
        "Q5992927": { "claims": { "P463": [ MOORE ] } }
      }
    }
    """;

    [Fact]
    public void A_member_whose_own_list_ends_a_year_before_the_band_split_stays()
    {
        var body = Beatles
            .Replace("BAND", string.Join(", ",
                Claim("Q1203", "1970", "1960"), Claim("Q2599", "1970", "1960"), Claim("Q207335", "1962", "1960"),
                Claim("Q5087041", "1961", "1960"), Claim("Q5992927", "1960", "1960")))
            .Replace("LENNON", Claim("Q1299", "1969", "1960"))
            .Replace("NEWBY", Claim("Q1299", "1961", "1960") + ", " + Claim("Q1299"))
            .Replace("MOORE", Claim("Q1299"));
        BandMember[] members =
        [
            new("John Lennon", "Q1203"), new("Paul McCartney", "Q2599"), new("Pete Best", "Q207335"),
            new("Chas Newby", "Q5087041"), new("Tommy Moore", "Q5992927"),
        ];

        var current = EncyclopediaLookup.CurrentLineup(body, "Q1299", members);

        Assert.Equal(["John Lennon", "Paul McCartney"], current.Select(m => m.Name));
    }

    /// <summary>An open membership with no start, beside an ended one, is a gap in the data and not a
    /// return.</summary>
    [Fact]
    public void An_open_membership_with_no_start_is_not_a_return()
    {
        var body = Answer().Replace(Claim("Q12", startYear: "1995"), Claim("Q12"));

        var current = EncyclopediaLookup.CurrentLineup(body, "Q1", Members);

        Assert.DoesNotContain(current, m => m.Name == "Came Back");
    }
}

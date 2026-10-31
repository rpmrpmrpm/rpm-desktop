using RPM.Core.Metadata.Providers;

namespace RPM.Core.Tests;

/// <summary>
/// The conductor on a classical record's credit, found on Wikidata by name, and only where the name and
/// the occupation both agree.
/// </summary>
public class ConductorLookupTests
{
    [Theory]
    [InlineData("Sir Colin Davis", "Colin Davis")]
    [InlineData("Herbert von Karajan", "Herbert von Karajan")]
    [InlineData("Dame Jane Glover", "Jane Glover")]
    [InlineData("Karl Böhm; Herbert von Karajan", "Karl Böhm")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void The_name_searched_for_is_the_first_conductor_without_the_knighthood(string? credit, string? name)
        => Assert.Equal(name, EncyclopediaLookup.ConductorName(credit));

    /// <summary>The search for "Colin Davis" as it answered on 2026-10-31, cut down: the conductor, whose
    /// aliases carry the knighthood, and a footballer of the same name who conducts nothing.</summary>
    private const string Entities = """
    {
      "entities": {
        "Q366624": {
          "labels": { "en": { "language": "en", "value": "Colin Davis" } },
          "aliases": { "en": [ { "language": "en", "value": "Sir Colin Davis" } ] },
          "claims": {
            "P106": [ { "mainsnak": { "datavalue": { "value": { "id": "Q158852" } } } },
                      { "mainsnak": { "datavalue": { "value": { "id": "Q486748" } } } } ],
            "P569": [ { "mainsnak": { "datavalue": { "value": { "time": "+1927-09-25T00:00:00Z", "precision": 11 } } } } ],
            "P570": [ { "mainsnak": { "datavalue": { "value": { "time": "+2013-04-14T00:00:00Z", "precision": 11 } } } } ],
            "P18": [ { "mainsnak": { "datavalue": { "value": "Sir Colin Davis.jpg" } } } ],
            "P1953": [ { "mainsnak": { "datavalue": { "value": "95546" } } } ]
          },
          "sitelinks": { "enwiki": {}, "dewiki": {}, "commonswiki": {} }
        },
        "Q5145441": {
          "labels": { "en": { "language": "en", "value": "Colin Davis" } },
          "claims": {
            "P106": [ { "mainsnak": { "datavalue": { "value": { "id": "Q937857" } } } } ]
          },
          "sitelinks": { "enwiki": {} }
        }
      }
    }
    """;

    [Fact]
    public void The_conductor_of_that_name_is_found_and_nobody_else()
    {
        var found = EncyclopediaLookup.PickConductor(Entities, ["Colin Davis", "Sir Colin Davis"])!;

        Assert.Equal("Q366624", found.Entity);
        Assert.Equal("1927", found.Born);
        Assert.Equal("2013", found.Died);
        Assert.Equal("Sir Colin Davis.jpg", found.Image);
        Assert.Equal("Q158852", found.Roles[0]);
        Assert.Equal(2, found.Articles);   // Commons is not a Wikipedia
        Assert.Contains(found.Links, l => l.Url == "https://www.discogs.com/artist/95546");
    }

    [Fact]
    public void A_name_nobody_answers_to_finds_nobody()
        => Assert.Null(EncyclopediaLookup.PickConductor(Entities, ["Colin Davies"]));
}

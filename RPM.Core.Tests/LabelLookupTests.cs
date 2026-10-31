using RPM.Core.Metadata.Providers;

namespace RPM.Core.Tests;

/// <summary>
/// The record label a release's tags name, found on Wikidata by name, and only where the name and "record
/// label" both agree.
/// </summary>
public class LabelLookupTests
{
    [Theory]
    [InlineData("Philips Classics", "Philips Classics")]
    [InlineData("  Deutsche Grammophon ", "Deutsche Grammophon")]
    [InlineData("Philips; Decca", "Philips")]
    [InlineData("A&M Records", "A&M Records")]
    [InlineData("Sony/ATV", "Sony/ATV")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void The_name_searched_for_is_the_first_label(string? tag, string? name)
        => Assert.Equal(name, EncyclopediaLookup.LabelName(tag));

    [Theory]
    [InlineData("Warp", "Warp Records")]
    [InlineData("Def Jam", "Def Jam Recordings")]
    [InlineData("Philips Classics", "Philips Classics Records")]
    [InlineData("Deutsche Grammophon", "Deutsche Grammophon GmbH")]
    public void A_trailing_records_or_company_word_does_not_tell_two_names_apart(string tag, string filed)
        => Assert.Equal(EncyclopediaLookup.LabelKey(tag), EncyclopediaLookup.LabelKey(filed));

    [Fact]
    public void A_name_that_is_only_a_company_word_is_kept()
        => Assert.Equal("records", EncyclopediaLookup.LabelKey("Records"));

    /// <summary>The search for "Philips Classics" as it answered on 2026-10-31, cut down: the label, filed as
    /// "Philips Classics Records", and PolyGram, the group that owned it, which is a different name.</summary>
    private const string Entities = """
    {
      "entities": {
        "Q7185509": {
          "labels": { "en": { "language": "en", "value": "Philips Classics Records" } },
          "aliases": { "en": [ { "language": "en", "value": "Philips Classics" } ] },
          "claims": {
            "P31": [ { "mainsnak": { "datavalue": { "value": { "id": "Q18127" } } } } ],
            "P571": [ { "mainsnak": { "datavalue": { "value": { "time": "+1980-00-00T00:00:00Z", "precision": 9 } } } } ],
            "P576": [ { "mainsnak": { "datavalue": { "value": { "time": "+1999-00-00T00:00:00Z", "precision": 9 } } } } ],
            "P1955": [ { "mainsnak": { "datavalue": { "value": "89214" } } } ],
            "P966": [ { "mainsnak": { "datavalue": { "value": "21636b95-8e0e-4b80-9a46-e9987281b995" } } } ]
          },
          "sitelinks": { "enwiki": {}, "nlwiki": {}, "commonswiki": {} }
        },
        "Q700359": {
          "labels": { "en": { "language": "en", "value": "PolyGram" } },
          "claims": {
            "P31": [ { "mainsnak": { "datavalue": { "value": { "id": "Q18127" } } } } ],
            "P154": [ { "mainsnak": { "datavalue": { "value": "PolyGram wordmark.svg" } } } ]
          },
          "sitelinks": { "enwiki": {}, "dewiki": {}, "frwiki": {} }
        }
      }
    }
    """;

    [Fact]
    public void The_label_of_that_name_is_found_and_not_its_owner()
    {
        var found = EncyclopediaLookup.PickLabel(Entities, "Philips Classics")!;

        Assert.Equal("Q7185509", found.Entity);
        Assert.Equal("Philips Classics Records", found.Label);
        Assert.Equal("1980", found.Founded);
        Assert.Equal("1999", found.Ended);
        Assert.Null(found.Logo);
        Assert.Equal(2, found.Articles);   // Commons is not a Wikipedia
        Assert.Contains(found.Links, l => l.Url == "https://www.discogs.com/label/89214");
        Assert.Contains(found.Links, l => l.Url == "https://musicbrainz.org/label/21636b95-8e0e-4b80-9a46-e9987281b995");
    }

    [Fact]
    public void The_name_as_the_encyclopedia_files_it_is_found_too()
        => Assert.Equal("Q7185509", EncyclopediaLookup.PickLabel(Entities, "Philips Classics Records")?.Entity);

    [Fact]
    public void A_name_no_label_answers_to_finds_nothing()
        => Assert.Null(EncyclopediaLookup.PickLabel(Entities, "Philips"));
}

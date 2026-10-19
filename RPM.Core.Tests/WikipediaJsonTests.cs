using RPM.Core.Metadata.Providers;

namespace RPM.Core.Tests;

/// <summary>
/// The route from a Wikidata entity to a paragraph of prose: which wiki has the article, and what
/// the article's opening says. Both ends have a trap in them: Wikidata keeps sister projects in
/// the same dictionary as the Wikipedias, and the MediaWiki API answers for a page that does not
/// exist with a perfectly well-formed body.
/// </summary>
public class WikipediaJsonTests
{
    private const string Sitelinks = """
    {
      "entities": {
        "Q170572": {
          "id": "Q170572",
          "sitelinks": {
            "commonswiki": { "site": "commonswiki", "title": "Category:Fleetwood Mac" },
            "dewiki": { "site": "dewiki", "title": "Fleetwood Mac" },
            "enwiki": { "site": "enwiki", "title": "Fleetwood Mac" },
            "enwikiquote": { "site": "enwikiquote", "title": "Fleetwood Mac" },
            "zhwiki": { "site": "zhwiki", "title": "佛利伍麥克" }
          }
        }
      }
    }
    """;

    /// <summary>A reader's own language first, English behind it. English is where the article usually
    /// is, so it is a fallback rather than a preference.</summary>
    [Fact]
    public void The_first_language_with_an_article_wins() =>
        Assert.Equal(("de", "Fleetwood Mac"), WikipediaJson.ParseSitelink(Sitelinks, ["de", "en"]));

    [Fact]
    public void A_language_with_no_article_falls_through_to_the_next() =>
        Assert.Equal(("en", "Fleetwood Mac"), WikipediaJson.ParseSitelink(Sitelinks, ["fr", "en"]));

    /// <summary>The app's Chinese is "zh-Hans" and Wikidata's key is "zhwiki", so the region has to go
    /// or nothing matches at all.</summary>
    [Fact]
    public void A_regional_language_code_is_reduced_to_its_wiki() =>
        Assert.Equal(("zh", "佛利伍麥克"), WikipediaJson.ParseSitelink(Sitelinks, ["zh-Hans", "en"]));

    /// <summary>Wikiquote and Commons sit in the same dictionary and are not Wikipedia. Only an exact
    /// "{language}wiki" counts: a French reader with no French article gets nothing, not a photo
    /// category or a page of quotations.</summary>
    [Fact]
    public void Sister_projects_are_not_wikipedia() =>
        Assert.Null(WikipediaJson.ParseSitelink(Sitelinks, ["fr"]));

    [Fact]
    public void An_entity_with_no_articles_at_all_answers_nothing() =>
        Assert.Null(WikipediaJson.ParseSitelink("""{ "entities": { "Q1": { "sitelinks": {} } } }""", ["en"]));

    [Theory]
    [InlineData("zh-Hans", "zh")]
    [InlineData("pt_BR", "pt")]
    [InlineData("EN", "en")]
    [InlineData("  ", "")]
    public void A_language_code_is_reduced_to_its_base(string given, string expected) =>
        Assert.Equal(expected, WikipediaJson.BaseLanguage(given));

    // ── the article ──

    private const string Extract = """
    {
      "query": {
        "pages": {
          "11891": {
            "pageid": 11891,
            "title": "Fleetwood Mac",
            "fullurl": "https://en.wikipedia.org/wiki/Fleetwood_Mac",
            "thumbnail": { "source": "https://upload.wikimedia.org/thumb.jpg", "width": 480 },
            "extract": "Fleetwood Mac are a British-American rock band.\nThe band was formed in London in 1967.\n\n"
          }
        }
      }
    }
    """;

    [Fact]
    public void The_lead_section_is_read_with_the_page_it_came_from()
    {
        var article = WikipediaJson.ParseExtract(Extract, "en");

        Assert.NotNull(article);
        Assert.Equal("Fleetwood Mac", article.Title);
        Assert.Equal("en", article.Language);
        Assert.Equal("https://en.wikipedia.org/wiki/Fleetwood_Mac", article.Url);
        Assert.Equal("https://upload.wikimedia.org/thumb.jpg", article.ThumbnailUrl);
    }

    /// <summary>The API separates paragraphs with one newline and leaves trailing whitespace behind.
    /// The pane shows paragraphs, so they are separated by a blank line and the trailing whitespace
    /// is trimmed.</summary>
    [Fact]
    public void The_extract_comes_back_as_paragraphs() =>
        Assert.Equal(
            "Fleetwood Mac are a British-American rock band.\n\nThe band was formed in London in 1967.",
            WikipediaJson.ParseExtract(Extract, "en")!.Extract);

    [Fact]
    public void The_pane_can_show_the_first_paragraph_and_offer_the_rest()
    {
        var article = WikipediaJson.ParseExtract(Extract, "en")!;

        Assert.Equal("Fleetwood Mac are a British-American rock band.", article.Lead);
        Assert.True(article.HasMore);
    }

    [Fact]
    public void A_one_paragraph_article_has_nothing_more_to_offer()
    {
        var article = new EncyclopediaArticle("en", "A", "One paragraph.", "https://example");

        Assert.Equal("One paragraph.", article.Lead);
        Assert.False(article.HasMore);
    }

    /// <summary>A page that does not exist comes back as a well-formed body with a negative id and a
    /// "missing" marker, not as an error, so nothing but the marker distinguishes it.</summary>
    [Fact]
    public void A_missing_page_is_no_article() =>
        Assert.Null(WikipediaJson.ParseExtract("""
        { "query": { "pages": { "-1": { "ns": 0, "title": "Nope", "missing": "" } } } }
        """, "en"));

    /// <summary>A disambiguation page is a list of other articles. Under an artist's name it reads as
    /// nonsense. It is recognised by the page property and not by reading the prose, which would be a
    /// different rule in every language.</summary>
    [Fact]
    public void A_disambiguation_page_is_not_an_article() =>
        Assert.Null(WikipediaJson.ParseExtract("""
        {
          "query": {
            "pages": {
              "21492": {
                "title": "Nirvana",
                "pageprops": { "disambiguation": "" },
                "extract": "Nirvana may refer to:"
              }
            }
          }
        }
        """, "en"));

    /// <summary>Without fullurl the address is built from the title, so an article still opens.</summary>
    [Fact]
    public void An_article_with_no_address_gets_one_built_from_its_title() =>
        Assert.Equal("https://fr.wikipedia.org/wiki/Fleetwood_Mac", WikipediaJson.ParseExtract("""
        { "query": { "pages": { "1": { "title": "Fleetwood Mac", "extract": "Un groupe." } } } }
        """, "fr")!.Url);
}

using RPM.Fingerprint.Catalog;
using RPM.Fingerprint.External;

namespace RPM.Core.Tests;

/// <summary>
/// Reading MusicBrainz's artists and release groups, against the shapes the service really returns.
/// The search endpoint and the lookup endpoint describe the same entity differently (one scores its
/// hits and includes nothing extra, the other has relations and genres and no score), and the
/// difference only shows when a record with an unusual shape comes through. These payloads are such
/// records.
/// </summary>
public class MusicBrainzEncyclopediaTests
{
    // ── the artist search ──

    private const string ArtistSearch = """
    {
      "count": 2,
      "artists": [
        {
          "id": "5b11f4ce-a62d-471e-81fc-a69a8278c7da",
          "score": 61,
          "name": "Nirvana",
          "type": "Group",
          "country": "GB",
          "disambiguation": "60s band from the UK",
          "area": { "name": "United Kingdom" },
          "life-span": { "begin": "1965", "end": "1971", "ended": true }
        },
        {
          "id": "9282c8b4-ca0b-4c6b-b7e3-4f7762dfc4d6",
          "score": 100,
          "name": "Nirvana",
          "type": "Group",
          "country": "US",
          "disambiguation": "90s grunge band",
          "area": { "name": "United States" },
          "begin-area": { "name": "Aberdeen" },
          "life-span": { "begin": "1987-88", "end": "1994-04-05", "ended": true }
        }
      ]
    }
    """;

    /// <summary>Two bands share the name, and the index's own relevance score is a much better guide
    /// to which one was meant than anything that can be worked out from the fields. It is second here
    /// on purpose: the service does not sort its own answer.</summary>
    [Fact]
    public void The_search_hands_its_best_hit_back_first()
    {
        var found = MusicBrainzEncyclopedia.ParseArtistSearch(ArtistSearch);

        Assert.Equal(2, found.Count);
        Assert.Equal("90s grunge band", found[0].Disambiguation);
        Assert.Equal("US", found[0].Country);
    }

    /// <summary>Where a band is FROM is more interesting than the country the catalogue files them
    /// under, and the two are different fields, so a begin-area, when there is one, is used.</summary>
    [Fact]
    public void Where_they_started_beats_where_they_are_filed()
    {
        var found = MusicBrainzEncyclopedia.ParseArtistSearch(ArtistSearch);

        Assert.Equal("Aberdeen", found[0].Area);
        Assert.Equal("United Kingdom", found[1].Area);   // no begin-area on the 60s band
    }

    [Fact]
    public void A_life_span_is_read_whole()
    {
        var band = MusicBrainzEncyclopedia.ParseArtistSearch(ArtistSearch)[0];

        Assert.Equal("1987-88", band.Began);
        Assert.Equal("1994-04-05", band.Ended);
        Assert.True(band.HasEnded);
    }

    [Fact]
    public void A_search_that_matched_nothing_is_an_empty_list() =>
        Assert.Empty(MusicBrainzEncyclopedia.ParseArtistSearch("""{ "count": 0, "artists": [] }"""));

    // ── the artist lookup ──

    private const string ArtistLookup = """
    {
      "id": "bd13909f-1c29-4c27-a874-d4aaf27c5b1a",
      "name": "Fleetwood Mac",
      "type": "Group",
      "country": "GB",
      "area": { "name": "United Kingdom" },
      "begin-area": { "name": "London" },
      "life-span": { "begin": "1967-07", "end": null, "ended": false },
      "genres": [
        { "name": "pop rock", "count": 9 },
        { "name": "blues rock", "count": 3 },
        { "name": "soft rock", "count": 12 }
      ],
      "relations": [
        { "type": "wikidata", "url": { "resource": "https://www.wikidata.org/wiki/Q170572" } },
        { "type": "official homepage", "url": { "resource": "https://fleetwoodmac.com/" } },
        { "type": "official homepage", "ended": true,
          "url": { "resource": "https://old.fleetwoodmac.example/" } },
        { "type": "discogs", "url": { "resource": "https://www.discogs.com/artist/4664" } },
        { "type": "social network", "url": { "resource": "https://twitter.com/fleetwoodmac" } },
        { "type": "artist" }
      ]
    }
    """;

    [Fact]
    public void A_lookup_reads_the_artist_whole()
    {
        var artist = MusicBrainzEncyclopedia.ParseArtist(ArtistLookup);

        Assert.NotNull(artist);
        Assert.Equal("Fleetwood Mac", artist.Name);
        Assert.Equal("Group", artist.Kind);
        Assert.Equal("London", artist.Area);
        Assert.Equal("1967-07", artist.Began);
        Assert.Null(artist.Ended);
        Assert.False(artist.HasEnded);
    }

    /// <summary>Most-voted first: a genre three people agreed on says less about a band than one twelve
    /// agreed on, and a pane has room for the top of the list, not all of it.</summary>
    [Fact]
    public void Genres_come_back_in_the_order_people_voted_for_them() =>
        Assert.Equal(["soft rock", "pop rock", "blues rock"], MusicBrainzEncyclopedia.ParseArtist(ArtistLookup)!.Genres);

    /// <summary>A homepage MusicBrainz has marked as ended is a dead link, and a dead link offered as a
    /// source is worse than no source. A relation with no URL at all is not a link either.</summary>
    [Fact]
    public void Ended_and_urlless_relations_never_reach_the_row()
    {
        var links = MusicBrainzEncyclopedia.ParseArtist(ArtistLookup)!.Links!;

        Assert.DoesNotContain(links, l => l.Url.Contains("old.fleetwoodmac", StringComparison.Ordinal));
        Assert.All(links, l => Assert.NotEqual("", l.Url));
    }

    /// <summary>The addresses are returned raw and in the catalogue's own order, with its label on
    /// each. Which SITE each one is on, and which of two Discogs pages the row shows, is decided by the
    /// app (see the classification test in <c>EncyclopediaLookupTests</c>). The label is kept because
    /// an artist's own homepage can be on any host.</summary>
    [Fact]
    public void The_addresses_arrive_raw_and_labelled_in_the_catalogues_order()
    {
        var links = MusicBrainzEncyclopedia.ParseArtist(ArtistLookup)!.Links!;

        Assert.Equal(
            ["https://www.wikidata.org/wiki/Q170572", "https://fleetwoodmac.com/",
             "https://www.discogs.com/artist/4664", "https://twitter.com/fleetwoodmac"],
            links.Select(l => l.Url));

        Assert.Equal("official homepage", links[1].Relation);
    }

    [Fact]
    public void Something_that_is_not_an_artist_at_all_parses_to_nothing() =>
        Assert.Null(MusicBrainzEncyclopedia.ParseArtist("""{ "error": "Not found" }"""));

    // ── the release group ──

    private const string ReleaseGroupSearch = """
    {
      "release-groups": [
        {
          "id": "5e5d0a49-3f5e-3b21-9b5b-cbc8f1a4d3e0",
          "score": 74,
          "title": "Rumours Live",
          "primary-type": "Album",
          "secondary-types": ["Live"],
          "first-release-date": "2023-09-08",
          "artist-credit": [
            { "name": "Fleetwood Mac",
              "artist": { "id": "bd13909f-1c29-4c27-a874-d4aaf27c5b1a", "name": "Fleetwood Mac" } }
          ]
        },
        {
          "id": "b8048f24-c026-3398-b23a-b5e50716cbc7",
          "score": 100,
          "title": "Rumours",
          "primary-type": "Album",
          "secondary-types": [],
          "first-release-date": "1977-02-04",
          "artist-credit": [
            { "name": "Fleetwood Mac",
              "artist": { "id": "bd13909f-1c29-4c27-a874-d4aaf27c5b1a", "name": "Fleetwood Mac" } }
          ]
        }
      ]
    }
    """;

    [Fact]
    public void The_best_scoring_record_leads_the_search()
    {
        var found = MusicBrainzEncyclopedia.ParseReleaseGroupSearch(ReleaseGroupSearch);

        Assert.Equal("Rumours", found[0].Title);
        Assert.Equal("1977-02-04", found[0].FirstReleased);
        Assert.Equal("Fleetwood Mac", found[0].Artist);
        Assert.Empty(found[0].SecondaryTypes);
        Assert.Equal(["Live"], found[1].SecondaryTypes);
    }

    /// <summary>
    /// The reason the album is looked up before the artist: a release-group search names its artist
    /// WITH their id, so the record the user owns identifies the artist, instead of a name that four
    /// bands share.
    /// </summary>
    [Fact]
    public void The_search_names_the_artist_who_made_the_best_match() =>
        Assert.Equal("bd13909f-1c29-4c27-a874-d4aaf27c5b1a",
            MusicBrainzEncyclopedia.ParseCreditedArtistMbid(ReleaseGroupSearch));

    /// <summary>The same call must also handle a lookup, which returns one release group, not a
    /// list of them.</summary>
    [Fact]
    public void A_single_release_group_names_its_artist_too() =>
        Assert.Equal("bd13909f-1c29-4c27-a874-d4aaf27c5b1a", MusicBrainzEncyclopedia.ParseCreditedArtistMbid("""
        {
          "id": "b8048f24-c026-3398-b23a-b5e50716cbc7",
          "title": "Rumours",
          "artist-credit": [
            { "name": "Fleetwood Mac",
              "artist": { "id": "bd13909f-1c29-4c27-a874-d4aaf27c5b1a", "name": "Fleetwood Mac" } }
          ]
        }
        """));

    [Fact]
    public void A_record_nobody_is_credited_on_names_nobody() =>
        Assert.Null(MusicBrainzEncyclopedia.ParseCreditedArtistMbid("""{ "id": "x", "title": "Untitled" }"""));

    [Fact]
    public void A_release_group_lookup_carries_its_pages_and_genres()
    {
        var album = MusicBrainzEncyclopedia.ParseReleaseGroup("""
        {
          "id": "b8048f24-c026-3398-b23a-b5e50716cbc7",
          "title": "Rumours",
          "primary-type": "Album",
          "first-release-date": "1977-02-04",
          "disambiguation": "",
          "genres": [{ "name": "soft rock", "count": 4 }],
          "relations": [
            { "type": "wikidata", "url": { "resource": "https://www.wikidata.org/wiki/Q633317" } },
            { "type": "discogs", "url": { "resource": "https://www.discogs.com/master/23934" } }
          ]
        }
        """);

        Assert.NotNull(album);
        Assert.Equal("Rumours", album.Title);
        Assert.Equal("Album", album.PrimaryType);
        Assert.Null(album.Disambiguation);   // present but blank is the same as absent
        Assert.Equal(["soft rock"], album.Genres);
        Assert.Equal(
            ["https://www.wikidata.org/wiki/Q633317", "https://www.discogs.com/master/23934"],
            album.Links!.Select(l => l.Url));
    }
}

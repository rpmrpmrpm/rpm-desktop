using RPM.Fingerprint.External;

namespace RPM.Core.Tests;

/// <summary>
/// The record collector's filters (catalogue number, country and year) reaching the MusicBrainz
/// query in the right form. A pressing is told apart from its siblings by these, so a wrong Lucene
/// field or a missing quote gives a filter that quietly does nothing.
/// </summary>
public class ReleaseFacetTests
{
    [Fact]
    public void The_album_alone_is_just_the_escaped_text()
    {
        Assert.Equal("rumours", MusicBrainzClient.BuildReleaseSearchQuery("rumours"));
    }

    [Fact]
    public void The_catalogue_number_is_a_quoted_catno_phrase()
    {
        // Quoted because a catalogue number is a phrase with spaces ("SHVL 804"), and an unquoted
        // Lucene term would split it and match neither half.
        var q = MusicBrainzClient.BuildReleaseSearchQuery("dark side of the moon", catalogNumber: "SHVL 804");
        Assert.Contains("catno:\"SHVL 804\"", q);
    }

    [Fact]
    public void Country_and_year_reach_their_own_fields()
    {
        var q = MusicBrainzClient.BuildReleaseSearchQuery("rumours", country: "GB", year: 1977);
        Assert.Contains("country:GB", q);
        // A range covering the year, not a wildcard: MusicBrainz indexes date as a date field, where a
        // prefix matches NOTHING ("date:1977*" returns zero releases). A wildcard would make the
        // search worse when the year is known than when it is not.
        Assert.Contains("date:[1977 TO 1977]", q);
    }

    [Fact]
    public void The_facets_and_the_artist_are_all_anded_together()
    {
        var q = MusicBrainzClient.BuildReleaseSearchQuery(
            "rumours", artist: "Fleetwood Mac", catalogNumber: "K 56344", country: "GB", year: 1977);

        Assert.Contains("artist:\"Fleetwood Mac\"", q);
        Assert.Contains("catno:\"K 56344\"", q);
        Assert.Contains("country:GB", q);
        Assert.Contains("date:[1977 TO 1977]", q);
        Assert.Equal(4, q.Split(" AND ").Length - 1);   // album + four ANDed facets
    }

    [Fact]
    public void An_artist_alone_is_a_whole_query_rather_than_a_dangling_AND()
    {
        // Clearing the album box and searching an artist: the query has to START with the artist
        // clause. A leading " AND artist:…" is a Lucene syntax error, which comes back as no results
        // and looks as if the album were still being searched.
        var q = MusicBrainzClient.BuildReleaseSearchQuery("", artist: "The Corrs");

        Assert.Equal("artist:\"The Corrs\"", q);
    }

    [Fact]
    public void A_catalogue_number_alone_is_a_whole_query_too()
    {
        var q = MusicBrainzClient.BuildReleaseSearchQuery("", catalogNumber: "SHVL 804");
        Assert.Equal("catno:\"SHVL 804\"", q);
    }

    [Fact]
    public void An_absent_facet_adds_nothing()
    {
        var q = MusicBrainzClient.BuildReleaseSearchQuery("rumours", country: "", year: 0);
        Assert.DoesNotContain("country:", q);
        Assert.DoesNotContain("date:", q);
    }
}

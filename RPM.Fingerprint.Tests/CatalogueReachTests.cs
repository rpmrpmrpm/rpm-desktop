using RPM.Fingerprint.External;

namespace RPM.Fingerprint.Tests;

/// <summary>
/// The rule that only the RPM API may send a request to a music catalogue.
///
/// <para>This test pins which hosts the rule covers and which it deliberately does not. Both halves
/// matter. If the rule is too narrow, it is broken as soon as someone writes a URL by hand. If it is
/// too wide, it blocks the app from reading Wikipedia in the reader's own language, or from fetching a
/// picture the user chose. Neither of those is a catalogue call.</para>
///
/// <para><b>Nothing here says <c>ThisProcessIsTheApi</c>.</b> That is a fact about the process, not
/// about one call. Saying it in one test would say it for every test that runs after it, and those
/// tests would stop checking anything. The permitted side is stated once, in the API's own startup,
/// and it is covered by the API answering at all.</para>
/// </summary>
public class CatalogueReachTests
{
    [Theory]
    [InlineData("https://musicbrainz.org/ws/2/artist/?query=x")]
    [InlineData("https://api.listenbrainz.org/1/popularity/recording")]
    [InlineData("https://labs.api.listenbrainz.org/recording-mbid-lookup/json")]
    [InlineData("https://api.acoustid.org/v2/lookup")]
    [InlineData("https://api.audd.io/")]
    [InlineData("https://www.theaudiodb.com/api/v1/json/2/artist.php")]
    [InlineData("https://api.discogs.com/database/search")]
    [InlineData("https://api.sonovault.now/v1/artists/search")]
    public void A_catalogue_is_refused(string url)
    {
        Assert.True(CatalogueReach.IsCatalogue(new Uri(url)));
        Assert.True(CatalogueReach.Refuses(new Uri(url)));
        Assert.Throws<CatalogueReachedDirectlyException>(() => CatalogueReach.Check(new Uri(url)));
    }

    /// <summary>
    /// What the app may still reach, and why each one is not a catalogue.
    ///
    /// <para>Wikipedia and Wikidata are allowed because which article to read depends on the reader's
    /// own language. An answer cached for everybody could only be cached in one language. The Cover Art
    /// Archive and the artwork hosts carry image bytes, not answers, and RPM does not host copyrighted
    /// pictures. fanart.tv's API is not on this list. The API calls it with RPM's project key (see
    /// <c>What_the_api_alone_asks_includes_fanart</c>).</para>
    /// </summary>
    [Theory]
    [InlineData("https://en.wikipedia.org/w/api.php")]
    [InlineData("https://www.wikidata.org/w/api.php")]
    [InlineData("https://coverartarchive.org/release/1/front-1200")]
    [InlineData("https://itunes.apple.com/search")]
    [InlineData("https://api.rpmrpmrpm.com/metadata/papers")]
    public void What_is_not_a_catalogue_is_left_alone(string url)
    {
        Assert.False(CatalogueReach.IsCatalogue(new Uri(url)));
        CatalogueReach.Check(new Uri(url));   // does not throw
    }

    /// <summary>fanart.tv's API is asked by the API on RPM's project key, with the user's personal key
    /// added there. An app calling it directly would spend the project key's quota outside the API's
    /// cache. Its pictures, on <c>assets.fanart.tv</c>, stay reachable (see
    /// <c>A_host_that_is_not_a_catalogue_needs_no_exception</c> below).</summary>
    [Fact]
    public void What_the_api_alone_asks_includes_fanart()
        => Assert.True(CatalogueReach.IsCatalogue(new Uri("https://webservice.fanart.tv/v3/music/1")));

    /// <summary>A subdomain is the same service. Matched on the label boundary rather than on the text,
    /// so a new host at MusicBrainz cannot slip past by being spelled differently.</summary>
    [Theory]
    [InlineData("https://beta.musicbrainz.org/ws/2/release/x")]
    [InlineData("https://test.api.listenbrainz.org/1/x")]
    public void A_subdomain_of_a_catalogue_is_the_catalogue(string url)
        => Assert.True(CatalogueReach.IsCatalogue(new Uri(url)));

    /// <summary>And a host that merely ENDS in the same letters is not. "notmusicbrainz.org" is
    /// somebody else, and refusing it would be refusing a service nobody named.</summary>
    [Theory]
    [InlineData("https://notmusicbrainz.org/x")]
    [InlineData("https://musicbrainz.org.example.com/x")]
    public void A_host_that_only_looks_alike_is_not_a_catalogue(string url)
        => Assert.False(CatalogueReach.IsCatalogue(new Uri(url)));

    /// <summary>The message names the host and says where the call belongs, because the person reading
    /// it is holding a stack trace and needs to know what to build instead.</summary>
    [Fact]
    public void The_refusal_says_what_to_do_instead()
    {
        var thrown = Assert.Throws<CatalogueReachedDirectlyException>(
            () => CatalogueReach.Check(new Uri("https://musicbrainz.org/ws/2/release/x")));

        Assert.Contains("musicbrainz.org", thrown.Message);
        Assert.Contains("RPM API", thrown.Message);
    }

    // ── pictures, which are not answers ──

    /// <summary>Artwork CDNs are outside the rule. TheAudioDB serves its pictures from an object store
    /// on the same domain as its API. Matching on the host alone would block those pictures too, so an
    /// artist photographed only by that source could never be fetched.</summary>
    [Theory]
    [InlineData("https://r2.theaudiodb.com/images/media/artist/fanart/twwyxt1561072641.jpg")]
    [InlineData("https://www.theaudiodb.com/images/media/artist/thumb/abc.jpg")]
    public void A_picture_on_a_catalogues_own_cdn_is_not_a_catalogue_call(string url)
        => Assert.False(CatalogueReach.Refuses(new Uri(url)));

    /// <summary>And the answers on the same host are still refused: it is the path that tells them
    /// apart, not the host.</summary>
    [Fact]
    public void An_answer_on_that_same_host_is_still_refused()
        => Assert.True(CatalogueReach.Refuses(
            new Uri("https://www.theaudiodb.com/api/v1/json/2/search.php?s=enya")));

    /// <summary>The exception is a path on a catalogue host, so it cannot let a whole other host
    /// through.</summary>
    [Fact]
    public void A_host_that_is_not_a_catalogue_needs_no_exception()
        => Assert.False(CatalogueReach.Refuses(new Uri("https://assets.fanart.tv/fanart/music/x.jpg")));
}

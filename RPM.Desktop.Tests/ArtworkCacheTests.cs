using System.Net.Http;
using RPM.Core.Net;
using RPM.Desktop.Services.Art;

namespace RPM.Desktop.Tests;

/// <summary>
/// Where the artwork answers are filed in the app's web cache.
///
/// <para>Artwork answers use their own cache namespace (<c>ArtworkService.Answers</c>), separate from
/// the plain art cache. Answers cached before the server used fanart.tv have none of its backdrops,
/// squares or logos. The art cache keeps an answer for 30 days without asking again, and it is keyed on
/// the URL, which is the same as before. Removing the separate namespace would bring those old answers
/// back for up to a month.</para>
/// </summary>
public class ArtworkCacheTests
{
    private static HttpRequestMessage Lookup()
        => new(HttpMethod.Get, "https://api.rpmrpmrpm.com/artwork?artist=Enya");

    [Fact]
    public void An_artwork_answer_is_not_filed_where_the_old_answers_are()
        => Assert.NotEqual(
            WebCache.KeyFor(Lookup(), WebCachePolicy.For("art")),
            WebCache.KeyFor(Lookup(), ArtworkService.Answers));

    [Fact]
    public void It_is_still_art_and_kept_as_long()
    {
        var art = WebCachePolicy.For("art");

        Assert.Equal(art.Category, ArtworkService.Answers.Category);
        Assert.Equal(art.MaxAge, ArtworkService.Answers.MaxAge);
        Assert.Equal(art.MissMaxAge, ArtworkService.Answers.MissMaxAge);
    }
}

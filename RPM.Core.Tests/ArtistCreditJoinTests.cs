using System.Text.Json;
using RPM.Fingerprint.External;

namespace RPM.Core.Tests;

/// <summary>
/// A MusicBrainz artist credit read back as one line: the names with the join phrases between them, spaces
/// and all.
/// </summary>
public class ArtistCreditJoinTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Join_phrases_keep_their_spaces()
    {
        var track = Json("""
            {"artist-credit":[
              {"name":"Missy Elliott","joinphrase":" feat. ","artist":{"id":"a","name":"Missy Elliott","sort-name":"Elliott, Missy"}},
              {"name":"Redman","joinphrase":" & ","artist":{"id":"b","name":"Redman","sort-name":"Redman"}},
              {"name":"Method Man","joinphrase":"","artist":{"id":"c","name":"Method Man","sort-name":"Method Man"}}
            ]}
            """);

        var credit = MusicBrainzRelations.ArtistCredit(track);

        Assert.Equal("Missy Elliott feat. Redman & Method Man", credit.Name);
        Assert.Equal("Elliott, Missy feat. Redman & Method Man", credit.SortName);
        Assert.Equal("a", credit.Id);
    }

    [Fact]
    public void A_single_artist_has_nothing_after_the_name()
    {
        var track = Json("""{"artist-credit":[{"name":"Missy Elliott","joinphrase":"","artist":{"id":"a","name":"Missy Elliott"}}]}""");

        Assert.Equal("Missy Elliott", MusicBrainzRelations.ArtistCredit(track).Name);
    }

    [Fact]
    public void A_vocal_credit_that_names_no_voice_is_still_a_singer()
    {
        var recording = Json("""
            {"relations":[
              {"type":"vocal","target-type":"artist","attributes":[],"artist":{"name":"Tweet"}},
              {"type":"vocal","target-type":"artist","attributes":["lead vocals"],"artist":{"name":"Missy Elliott"}},
              {"type":"instrument","target-type":"artist","attributes":["piano"],"artist":{"name":"Craig Brockman"}}
            ]}
            """);

        Assert.Equal(
            "Tweet (vocals); Missy Elliott (lead vocals); Craig Brockman (piano)",
            MusicBrainzRelations.Recording(recording).Soloists);
    }
}

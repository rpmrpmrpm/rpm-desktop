using System.Text.Json;
using RPM.Fingerprint.External;

namespace RPM.Core.Tests;

/// <summary>
/// The work a recording is a performance of. MusicBrainz links nearly every song to a work of its own name,
/// and that is not a work in the classical sense: a pop album read that way arrived as twelve one-track works
/// and was ripped as a classical record.
/// </summary>
public class MusicBrainzWorkTests
{
    private static JsonElement Recording(string title, string work, string? parent = null, string? composer = null)
    {
        var relations = new List<string>();
        if (parent is not null)
            relations.Add("{\"type\":\"parts\",\"direction\":\"backward\",\"ordering-key\":1,\"work\":{\"title\":\"" + parent + "\"}}");
        if (composer is not null)
            relations.Add("{\"type\":\"composer\",\"artist\":{\"name\":\"" + composer + "\",\"sort-name\":\"" + composer + "\"}}");

        var json = "{\"title\":\"" + title + "\",\"relations\":[{\"type\":\"performance\",\"work\":{\"title\":\"" + work
                   + "\",\"relations\":[" + string.Join(",", relations) + "]}}]}";
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public void A_song_linked_to_a_work_of_its_own_name_has_no_work_but_keeps_its_writers()
    {
        var credits = MusicBrainzRelations.Work(Recording("Can’t Tell Me Nothing", "Can't Tell Me Nothing", composer: "Kanye West"));

        Assert.Null(credits.Work);
        Assert.Equal("Kanye West", credits.Composers);
    }

    [Fact]
    public void A_featured_artist_in_the_title_does_not_make_the_song_a_work()
        => Assert.Null(MusicBrainzRelations.Work(Recording("Homecoming (feat. Chris Martin)", "Homecoming")).Work);

    [Fact]
    public void A_movement_keeps_its_work()
    {
        var credits = MusicBrainzRelations.Work(Recording("I. Allegro", "I. Allegro", parent: "Symphony No. 5"));

        Assert.Equal("Symphony No. 5", credits.Work);
        Assert.Equal("I. Allegro", credits.MovementName);
    }

    [Fact]
    public void A_recording_named_for_its_work_and_movement_keeps_the_work()
        => Assert.Equal("Symphony No. 5",
            MusicBrainzRelations.Work(Recording("Symphony No. 5: I. Allegro con brio", "Symphony No. 5")).Work);
}

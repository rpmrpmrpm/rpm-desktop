using RPM.Core.Metadata.Providers;

namespace RPM.Core.Tests;

/// <summary>
/// The people on a Discogs release, and which tag each of them belongs in.
///
/// <para>Discogs gives every credit as free text in one list ("Composed By", "Fortepiano", "Liner
/// Notes") and lets a credit name the tracks it covers by position. Read through
/// <see cref="DiscogsJson.ParseRelease"/>, not by calling the sorting directly, because the ranges
/// only have meaning against a listing.</para>
/// </summary>
public class DiscogsCreditsTests
{
    /// <summary>The Mozart box's own credit list: the composer, the fortepianist who plays most of
    /// it, and the two people who wrote the booklet, who are not on the record.</summary>
    [Fact]
    public void A_release_credit_reaches_every_track_and_the_booklet_reaches_none()
    {
        var detail = DiscogsJson.ParseRelease("""
        {
          "id": 1, "title": "Complete Keyboard Works",
          "artists": [{ "name": "Wolfgang Amadeus Mozart" }],
          "extraartists": [
            { "name": "Wolfgang Amadeus Mozart", "role": "Composed By", "tracks": "" },
            { "name": "Bart van Oort", "role": "Fortepiano", "tracks": "" },
            { "name": "Bart van Oort", "role": "Liner Notes", "tracks": "" },
            { "name": "Sylvia Berry", "role": "Liner Notes", "tracks": "" }
          ],
          "tracklist": [
            { "type_": "track", "position": "1-1", "title": "Allegro" },
            { "type_": "track", "position": "1-2", "title": "Adagio" }
          ]
        }
        """, "1");

        Assert.All(detail.Tracks, t => Assert.Equal("Wolfgang Amadeus Mozart", t.Composers));
        Assert.All(detail.Tracks, t => Assert.Equal("Bart van Oort (fortepiano)", t.Soloists));
        Assert.All(detail.Tracks, t => Assert.Null(t.Conductor));
        Assert.DoesNotContain(detail.Tracks, t => t.Soloists?.Contains("Sylvia Berry") == true);
    }

    /// <summary>A credit can name the discs it plays on (here the box's second fortepianist, who
    /// plays discs 12 to 14), and the ranges use the listing's own positions.</summary>
    [Fact]
    public void A_credit_with_a_track_range_reaches_only_those_tracks()
    {
        var detail = DiscogsJson.ParseRelease("""
        {
          "id": 2, "title": "Complete Keyboard Works", "artists": [{ "name": "Mozart" }],
          "extraartists": [
            { "name": "Bart van Oort", "role": "Fortepiano", "tracks": "" },
            { "name": "Ursula Dütschler", "role": "Fortepiano", "tracks": "12-1 to 12-2, 14-1" }
          ],
          "tracklist": [
            { "type_": "track", "position": "11-1", "title": "One" },
            { "type_": "track", "position": "12-1", "title": "Two" },
            { "type_": "track", "position": "12-2", "title": "Three" },
            { "type_": "track", "position": "13-1", "title": "Four" },
            { "type_": "track", "position": "14-1", "title": "Five" }
          ]
        }
        """, "2");

        // Both are credited where the range covers, in the order the sleeve lists them.
        Assert.Equal("Bart van Oort (fortepiano)", detail.Tracks[0].Soloists);
        Assert.Equal("Bart van Oort (fortepiano), Ursula Dütschler (fortepiano)", detail.Tracks[1].Soloists);
        Assert.Equal("Bart van Oort (fortepiano), Ursula Dütschler (fortepiano)", detail.Tracks[2].Soloists);
        Assert.Equal("Bart van Oort (fortepiano)", detail.Tracks[3].Soloists);
        Assert.Equal("Bart van Oort (fortepiano), Ursula Dütschler (fortepiano)", detail.Tracks[4].Soloists);
    }

    /// <summary>A vinyl range can name a side as a whole ("B"), which is every position on it.</summary>
    [Fact]
    public void A_range_naming_a_side_covers_the_whole_side()
    {
        var detail = DiscogsJson.ParseRelease("""
        {
          "id": 3, "title": "Recital", "artists": [{ "name": "A" }],
          "extraartists": [{ "name": "Someone", "role": "Harpsichord", "tracks": "B" }],
          "tracklist": [
            { "type_": "track", "position": "A1", "title": "One" },
            { "type_": "track", "position": "B1", "title": "Two" },
            { "type_": "track", "position": "B2", "title": "Three" }
          ]
        }
        """, "3");

        Assert.Null(detail.Tracks[0].Soloists);
        Assert.Equal("Someone (harpsichord)", detail.Tracks[1].Soloists);
        Assert.Equal("Someone (harpsichord)", detail.Tracks[2].Soloists);
    }

    /// <summary>Each named role goes to the tag it belongs in, and a chorus master conducts rather
    /// than being an ensemble.</summary>
    [Fact]
    public void Named_roles_land_in_their_own_tags()
    {
        var detail = DiscogsJson.ParseRelease("""
        {
          "id": 4, "title": "Messiah", "artists": [{ "name": "Handel" }],
          "extraartists": [
            { "name": "George Frideric Handel", "role": "Composed By" },
            { "name": "Charles Jennens", "role": "Libretto By" },
            { "name": "Wolfgang Amadeus Mozart", "role": "Arranged By" },
            { "name": "John Eliot Gardiner", "role": "Conductor" },
            { "name": "Simon Halsey", "role": "Chorus Master" },
            { "name": "Monteverdi Choir", "role": "Choir" },
            { "name": "English Baroque Soloists", "role": "Orchestra" },
            { "name": "Anne Sofie von Otter", "role": "Mezzo-Soprano Vocals" },
            { "name": "Nigel Short", "role": "Mastered By" }
          ],
          "tracklist": [{ "type_": "track", "position": "1", "title": "Sinfonia" }]
        }
        """, "4");

        var track = detail.Tracks[0];
        Assert.Equal("George Frideric Handel", track.Composers);
        Assert.Equal("Charles Jennens", track.Lyricist);
        Assert.Equal("Wolfgang Amadeus Mozart", track.Arranger);
        Assert.Equal("John Eliot Gardiner, Simon Halsey", track.Conductor);
        Assert.Equal("Monteverdi Choir, English Baroque Soloists", track.Ensemble);
        Assert.Equal("Anne Sofie von Otter (mezzo-soprano vocals)", track.Soloists);
    }

    /// <summary>A person credited with two instruments is named once, as a sleeve prints it. The
    /// bracketed note on an instrument is about the instrument, not the credit.</summary>
    [Fact]
    public void One_person_playing_two_instruments_is_named_once()
    {
        var detail = DiscogsJson.ParseRelease("""
        {
          "id": 5, "title": "Recital", "artists": [{ "name": "A" }],
          "extraartists": [
            { "name": "Bart van Oort", "role": "Fortepiano [Anton Walter, 1795], Clavichord" }
          ],
          "tracklist": [{ "type_": "track", "position": "1", "title": "One" }]
        }
        """, "5");

        Assert.Equal("Bart van Oort (fortepiano, clavichord)", detail.Tracks[0].Soloists);
    }

    /// <summary>A credit on the track, or on the work its movement belongs to, is about that track
    /// and wins over the release's credit for the same role. The roles it does not mention still come
    /// from the release.</summary>
    [Fact]
    public void A_credit_on_the_work_beats_the_release_for_that_role()
    {
        var detail = DiscogsJson.ParseRelease("""
        {
          "id": 6, "title": "Recital", "artists": [{ "name": "A" }],
          "extraartists": [
            { "name": "House Pianist", "role": "Piano" },
            { "name": "Wolfgang Amadeus Mozart", "role": "Composed By" }
          ],
          "tracklist": [
            { "type_": "index", "title": "Sonata In C", "extraartists": [
                { "name": "Guest Pianist", "role": "Piano" }
              ], "sub_tracks": [
                { "position": "1", "type_": "track", "title": "Allegro" }
              ] },
            { "type_": "track", "position": "2", "title": "Encore" }
          ]
        }
        """, "6");

        Assert.Equal("Guest Pianist (piano)", detail.Tracks[0].Soloists);
        Assert.Equal("Wolfgang Amadeus Mozart", detail.Tracks[0].Composers);
        Assert.Equal("House Pianist (piano)", detail.Tracks[1].Soloists);
    }

    /// <summary>Discogs disambiguates people who share a name with a trailing number. It is a fact
    /// about their database, and it does not belong in a tag.</summary>
    [Fact]
    public void The_disambiguating_number_after_a_name_is_dropped()
    {
        var detail = DiscogsJson.ParseRelease("""
        {
          "id": 7, "title": "X", "artists": [{ "name": "A" }],
          "extraartists": [{ "name": "John Williams (2)", "role": "Guitar" }],
          "tracklist": [{ "type_": "track", "position": "1", "title": "One" }]
        }
        """, "7");

        Assert.Equal("John Williams (guitar)", detail.Tracks[0].Soloists);
    }

    /// <summary>A record with only production credits leaves every classical field null. Most records
    /// are like this, and an empty answer is not the same as a wrong one.</summary>
    [Fact]
    public void A_record_credited_only_to_its_makers_stamps_nothing()
    {
        var detail = DiscogsJson.ParseRelease("""
        {
          "id": 8, "title": "Rumours", "artists": [{ "name": "Fleetwood Mac" }],
          "extraartists": [
            { "name": "Ken Caillat", "role": "Producer, Engineer" },
            { "name": "Ken Perry", "role": "Mastered By" },
            { "name": "Desmond Strobel", "role": "Design" }
          ],
          "tracklist": [{ "type_": "track", "position": "A1", "title": "Second Hand News" }]
        }
        """, "8");

        var track = detail.Tracks[0];
        Assert.Null(track.Composers);
        Assert.Null(track.Soloists);
        Assert.Null(track.Conductor);
        Assert.Null(track.Ensemble);
    }
}

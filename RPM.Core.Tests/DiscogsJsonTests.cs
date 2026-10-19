using RPM.Core.Metadata.Providers;

namespace RPM.Core.Tests;

/// <summary>
/// Reading Discogs, against the shapes it actually returns. Its search and its release endpoint
/// disagree about nearly every field, so these tests use real payloads, not assertions that repeat
/// the parsing.
/// </summary>
public class DiscogsJsonTests
{
    // ── the release listing ──

    /// <summary>
    /// Discogs credits a TRACK only when its artist differs from the record's, so an ordinary album
    /// names its artist once (on the release) and every track's own credit is absent. Reading only
    /// the track would leave every row blank on the records most people own.
    /// </summary>
    [Fact]
    public void Every_track_of_a_single_artist_album_gets_the_release_artist()
    {
        var detail = DiscogsJson.ParseRelease("""
        {
          "id": 12345,
          "title": "Rumours",
          "year": 1977,
          "country": "UK",
          "artists": [{ "name": "Fleetwood Mac" }],
          "formats": [{ "name": "Vinyl", "descriptions": ["LP", "Album"] }],
          "tracklist": [
            { "type_": "track", "position": "A1", "title": "Second Hand News", "duration": "2:43" },
            { "type_": "track", "position": "A2", "title": "Dreams",           "duration": "4:14" }
          ]
        }
        """, "12345");

        Assert.Equal(2, detail.Tracks.Count);
        Assert.All(detail.Tracks, t => Assert.Equal("Fleetwood Mac", t.Artist));
        Assert.Equal("Fleetwood Mac", detail.Artist);
    }

    /// <summary>The other half of the same rule: where a track IS credited (a compilation, a split,
    /// a guest), its own credit wins over the record's.</summary>
    [Fact]
    public void A_track_with_its_own_credit_keeps_it()
    {
        var detail = DiscogsJson.ParseRelease("""
        {
          "id": 1, "title": "Now That's What I Call Music", "year": 1983,
          "artists": [{ "name": "Various" }],
          "tracklist": [
            { "type_": "track", "title": "Only You", "duration": "3:13", "artists": [{ "name": "Yazoo" }] },
            { "type_": "track", "title": "Temptation", "duration": "4:07" }
          ]
        }
        """, "1");

        Assert.Equal("Yazoo", detail.Tracks[0].Artist);
        Assert.Equal("Various", detail.Tracks[1].Artist);   // uncredited falls back to the record
    }

    /// <summary>A tracklist is not only tracks: it also has headings and index entries, and counting
    /// them makes the fit score wrong against what was detected in the audio.</summary>
    [Fact]
    public void Headings_in_a_tracklist_are_not_tracks()
    {
        var detail = DiscogsJson.ParseRelease("""
        {
          "id": 1, "title": "Tubular Bells", "artists": [{ "name": "Mike Oldfield" }],
          "tracklist": [
            { "type_": "heading", "title": "Part One" },
            { "type_": "track", "position": "A", "title": "Tubular Bells Part One", "duration": "25:30" },
            { "type_": "index",  "title": "Introduction" },
            { "type_": "track", "position": "B", "title": "Tubular Bells Part Two", "duration": "23:20" }
          ]
        }
        """, "1");

        Assert.Equal(2, detail.Tracks.Count);
        Assert.Equal(new uint[] { 1, 2 }, detail.Tracks.Select(t => t.Position).ToArray());
        Assert.Equal(1530, detail.Tracks[0].LengthSeconds);
    }

    [Fact]
    public void A_track_with_no_duration_is_null_not_zero()
    {
        var detail = DiscogsJson.ParseRelease("""
        {
          "id": 1, "title": "X", "artists": [{ "name": "A" }],
          "tracklist": [{ "type_": "track", "title": "Untimed", "duration": "" }]
        }
        """, "1");

        Assert.Null(detail.Tracks[0].LengthSeconds);
        Assert.Equal("A", detail.Tracks[0].Artist);
    }

    [Fact]
    public void A_release_states_its_year_as_a_number()
    {
        var detail = DiscogsJson.ParseRelease("""{"id":1,"title":"X","year":1977,"tracklist":[]}""", "1");
        Assert.Equal(1977u, detail.Year);
    }

    // ── multi-disc positions ──

    /// <summary>A multi-disc set writes the disc into the position ("2-3"). Each track keeps its disc
    /// and its number within that disc, while Position stays continuous across the whole release.</summary>
    [Fact]
    public void A_multi_disc_release_reads_disc_and_per_disc_number_from_the_position()
    {
        var detail = DiscogsJson.ParseRelease("""
        {
          "id": 9, "title": "The Wall", "year": 1979,
          "artists": [{ "name": "Pink Floyd" }],
          "tracklist": [
            { "type_": "track", "position": "1-1", "title": "In the Flesh?",      "duration": "3:19" },
            { "type_": "track", "position": "1-2", "title": "The Thin Ice",        "duration": "2:27" },
            { "type_": "track", "position": "2-1", "title": "Hey You",             "duration": "4:40" },
            { "type_": "track", "position": "2-2", "title": "Is There Anybody Out There?", "duration": "2:44" }
          ]
        }
        """, "9");

        Assert.Equal(2u, detail.DiscCount);
        Assert.Equal(new uint[] { 1, 1, 2, 2 }, detail.Tracks.Select(t => t.Disc).ToArray());
        Assert.Equal(new uint[] { 1, 2, 1, 2 }, detail.Tracks.Select(t => t.DiscPosition).ToArray());
        // Position is continuous across discs: the merged "one album" numbering.
        Assert.Equal(new uint[] { 1, 2, 3, 4 }, detail.Tracks.Select(t => t.Position).ToArray());
    }

    /// <summary>A single disc names no disc: it reports Disc 0, matching MusicBrainz, so the merged
    /// and separate modes both see "not a multi-disc release" the same way.</summary>
    [Fact]
    public void A_single_disc_release_carries_no_disc_number()
    {
        var detail = DiscogsJson.ParseRelease("""
        {
          "id": 1, "title": "Rumours", "artists": [{ "name": "Fleetwood Mac" }],
          "tracklist": [
            { "type_": "track", "position": "A1", "title": "Second Hand News", "duration": "2:43" },
            { "type_": "track", "position": "A2", "title": "Dreams",           "duration": "4:14" }
          ]
        }
        """, "1");

        Assert.Equal(1u, detail.DiscCount);
        Assert.All(detail.Tracks, t => Assert.Equal(0u, t.Disc));
    }

    /// <summary>A heading just before a disc's first track names that disc (a box set's "B-Sides").</summary>
    [Fact]
    public void A_heading_before_a_disc_becomes_its_subtitle()
    {
        var detail = DiscogsJson.ParseRelease("""
        {
          "id": 2, "title": "Greatest Hits", "artists": [{ "name": "A" }],
          "tracklist": [
            { "type_": "heading", "title": "The Hits" },
            { "type_": "track", "position": "1-1", "title": "One", "duration": "3:00" },
            { "type_": "heading", "title": "B-Sides & Rarities" },
            { "type_": "track", "position": "2-1", "title": "Two", "duration": "3:00" }
          ]
        }
        """, "2");

        Assert.Equal("The Hits", detail.Tracks[0].DiscSubtitle);
        Assert.Equal("B-Sides & Rarities", detail.Tracks[1].DiscSubtitle);
    }

    // ── works and their movements ──
    //
    // The payloads below have the shape Discogs returns for release 11240647, "Mozart, Bart van
    // Oort: Complete Keyboard Works", a 14-CD box whose first disc is a sonata, two fantasies and a
    // second sonata. Reading only the top level of the tracklist finds the two fantasies and loses
    // all six movements, so the disc imports as two tracks called "Fantasy" and six called "Track N".

    /// <summary>An entry with sub-tracks is a multi-part piece: its title is the work, and the
    /// movements inside it are the tracks, with their positions and durations.</summary>
    [Fact]
    public void The_movements_inside_a_work_are_tracks()
    {
        var detail = DiscogsJson.ParseRelease("""
        {
          "id": 11240647, "title": "Complete Keyboard Works",
          "artists": [{ "name": "Wolfgang Amadeus Mozart" }],
          "tracklist": [
            { "position": "", "type_": "index", "title": "Sonata In B-Flat Major, KV. 333 (1783-84)",
              "duration": "", "sub_tracks": [
                { "position": "1-1", "type_": "track", "title": "Allegro",             "duration": "7:03" },
                { "position": "1-2", "type_": "track", "title": "Andante Cantabile",   "duration": "8:12" },
                { "position": "1-3", "type_": "track", "title": "Allegretto Grazioso", "duration": "6:19" }
              ] },
            { "position": "1-4", "type_": "track", "title": "Fantasy In D Minor, KV. 397 (1782)", "duration": "7:20" },
            { "position": "1-5", "type_": "track", "title": "Fantasy In C Minor, KV. 396 (1782)", "duration": "7:29" }
          ]
        }
        """, "11240647");

        Assert.Equal(5, detail.Tracks.Count);
        Assert.Equal(new uint[] { 1, 2, 3, 4, 5 }, detail.Tracks.Select(t => t.DiscPosition).ToArray());
        Assert.Equal(423, detail.Tracks[0].LengthSeconds);

        // The work is stated once per movement, the movement named and numbered within it.
        Assert.Equal("Sonata In B-Flat Major, KV. 333 (1783-84)", detail.Tracks[1].Work);
        Assert.Equal("Andante Cantabile", detail.Tracks[1].MovementName);
        Assert.Equal(2u, detail.Tracks[1].MovementNumber);
        Assert.Equal(3u, detail.Tracks[1].MovementCount);

        // The title has the full name, for all later code that does not know about works.
        Assert.Equal("Sonata In B-Flat Major, KV. 333 (1783-84): Andante Cantabile", detail.Tracks[1].Title);

        // A piece that is not part of a work is unchanged.
        Assert.Null(detail.Tracks[3].Work);
        Assert.Null(detail.Tracks[3].MovementName);
        Assert.Equal(0u, detail.Tracks[3].MovementNumber);
        Assert.Equal("Fantasy In D Minor, KV. 397 (1782)", detail.Tracks[3].Title);
    }

    /// <summary>The movements carry the positions, so a box set's discs are read from them exactly as
    /// for a listing with no works in it.</summary>
    [Fact]
    public void A_work_split_across_a_box_set_keeps_its_discs()
    {
        var detail = DiscogsJson.ParseRelease("""
        {
          "id": 2, "title": "Complete Keyboard Works", "artists": [{ "name": "Mozart" }],
          "tracklist": [
            { "type_": "index", "title": "Sonata In C Major, KV. 330", "sub_tracks": [
                { "position": "1-6", "type_": "track", "title": "Allegro Moderato" },
                { "position": "1-7", "type_": "track", "title": "Andante Cantabile" }
              ] },
            { "type_": "index", "title": "Sonata In B-Flat Major, KV. 570", "sub_tracks": [
                { "position": "2-1", "type_": "track", "title": "Allegro" },
                { "position": "2-2", "type_": "track", "title": "Adagio" }
              ] }
          ]
        }
        """, "2");

        Assert.Equal(2u, detail.DiscCount);
        Assert.Equal(new uint[] { 1, 1, 2, 2 }, detail.Tracks.Select(t => t.Disc).ToArray());
        Assert.Equal(new uint[] { 1, 2, 1, 2 }, detail.Tracks.Select(t => t.DiscPosition).ToArray());
        Assert.Equal(new uint[] { 1, 2, 3, 4 }, detail.Tracks.Select(t => t.Position).ToArray());
        Assert.Equal(new uint[] { 1, 2, 1, 2 }, detail.Tracks.Select(t => t.MovementNumber).ToArray());
    }

    /// <summary>A movement whose name already starts with the work is left alone: some submitters
    /// write the full name into every row, and repeating it is not useful.</summary>
    [Fact]
    public void A_movement_already_naming_its_work_is_not_told_twice()
    {
        var detail = DiscogsJson.ParseRelease("""
        {
          "id": 3, "title": "X", "artists": [{ "name": "A" }],
          "tracklist": [
            { "type_": "index", "title": "Symphony No. 5", "sub_tracks": [
                { "position": "1", "type_": "track", "title": "Symphony No. 5: I. Allegro" }
              ] }
          ]
        }
        """, "3");

        Assert.Equal("Symphony No. 5: I. Allegro", detail.Tracks[0].Title);
        Assert.Equal("Symphony No. 5", detail.Tracks[0].Work);
    }

    /// <summary>An index that groups nothing is still not a track: a suite's name printed above a
    /// side is a heading, whatever type Discogs gives it.</summary>
    [Fact]
    public void An_index_with_no_sub_tracks_is_still_not_a_track()
    {
        var detail = DiscogsJson.ParseRelease("""
        {
          "id": 4, "title": "Tubular Bells", "artists": [{ "name": "Mike Oldfield" }],
          "tracklist": [
            { "type_": "index",  "title": "Introduction" },
            { "type_": "track", "position": "A", "title": "Tubular Bells Part One", "duration": "25:30" }
          ]
        }
        """, "4");

        Assert.Single(detail.Tracks);
        Assert.Null(detail.Tracks[0].Work);
    }

    [Theory]
    [InlineData("1-1", 1)]
    [InlineData("2-3", 2)]
    [InlineData("CD2-05", 2)]
    [InlineData("2.4", 2)]
    [InlineData("1", 0)]      // a bare number is a track on a single disc, not a disc
    [InlineData("A1", 0)]     // a vinyl side is not a disc number
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void ParseDiscNumber_only_reads_a_disc_from_a_compound_position(string? position, int expected) =>
        Assert.Equal((uint)expected, DiscogsJson.ParseDiscNumber(position));

    // ── the search ──

    /// <summary>Everything the search returns, including the artist joined to the front of the title
    /// and a cover it returns without a second request.</summary>
    [Fact]
    public void A_search_result_carries_the_pressing_and_its_sleeve()
    {
        var results = DiscogsJson.ParseSearch("""
        {
          "results": [
            {
              "id": 7654, "type": "release",
              "title": "Fleetwood Mac - Rumours",
              "year": "1977", "country": "UK",
              "format": ["Vinyl", "LP", "Album"],
              "label": ["Warner Bros. Records"],
              "catno": "K 56344",
              "thumb": "https://img.discogs.com/thumb.jpg",
              "cover_image": "https://img.discogs.com/cover.jpg"
            }
          ]
        }
        """);

        var r = Assert.Single(results);
        Assert.Equal("discogs", r.Ref.Provider);
        Assert.Equal("7654", r.Ref.Id);
        Assert.Equal("Rumours", r.Title);
        Assert.Equal("Fleetwood Mac", r.Artist);
        Assert.Equal(1977u, r.Year);            // a STRING here, a number on the release
        Assert.Equal("UK", r.Country);
        Assert.Equal("Warner Bros. Records", r.Label);
        Assert.Equal("K 56344", r.CatalogNumber);
        Assert.True(r.IsVinyl);
        Assert.Equal("https://img.discogs.com/thumb.jpg", r.CoverUrl);
    }

    /// <summary>A master is the abstract album, not a pressing. The lookup matches a physical record
    /// the user has.</summary>
    [Fact]
    public void Masters_are_not_pressings_and_are_skipped()
    {
        var results = DiscogsJson.ParseSearch("""
        {
          "results": [
            { "id": 1, "type": "master",  "title": "A - B" },
            { "id": 2, "type": "release", "title": "A - B" }
          ]
        }
        """);

        Assert.Equal("2", Assert.Single(results).Ref.Id);
    }

    /// <summary>Only the first " - " splits, or an album with a hyphen in its name loses half of it.</summary>
    [Fact]
    public void Only_the_first_separator_splits_the_title()
    {
        var results = DiscogsJson.ParseSearch("""
        {"results":[{"id":1,"type":"release","title":"Godspeed You! Black Emperor - F♯ A♯ ∞ - Reissue"}]}
        """);

        var r = Assert.Single(results);
        Assert.Equal("Godspeed You! Black Emperor", r.Artist);
        Assert.Equal("F♯ A♯ ∞ - Reissue", r.Title);
    }

    [Fact]
    public void A_title_with_no_separator_is_all_title()
    {
        var r = Assert.Single(DiscogsJson.ParseSearch("""{"results":[{"id":1,"type":"release","title":"Rumours"}]}"""));
        Assert.Null(r.Artist);
        Assert.Equal("Rumours", r.Title);
    }

    [Fact]
    public void The_first_result_is_the_most_relevant()
    {
        var results = DiscogsJson.ParseSearch("""
        {"results":[
          {"id":1,"type":"release","title":"A - X"},
          {"id":2,"type":"release","title":"A - Y"}
        ]}
        """, limit: 25);

        Assert.True(results[0].Relevance > results[1].Relevance);
    }

    [Fact]
    public void An_empty_answer_is_empty_not_a_crash()
    {
        Assert.Empty(DiscogsJson.ParseSearch("""{"results":[]}"""));
        Assert.Empty(DiscogsJson.ParseSearch("""{}"""));
    }

    // ── art ──

    private const string TwoImages = """
    {
      "images": [
        { "type": "secondary", "uri150": "https://img/label.jpg", "uri": "https://img/label-big.jpg" },
        { "type": "primary",   "uri150": "https://img/front.jpg", "uri": "https://img/front-big.jpg" }
      ]
    }
    """;

    [Fact]
    public void The_front_cover_is_the_primary_image_not_the_label_scan() =>
        Assert.Equal("https://img/front.jpg", DiscogsJson.FrontCoverUrl(TwoImages, maxSize: 150));

    /// <summary>Discogs offers each image at 150 px and full-size, and the two are not
    /// interchangeable: 150 px is right for a search row, but not for a sleeve. Asking for a real size
    /// must get the scan, because that is the picture that is embedded in the track files.
    /// </summary>
    [Fact]
    public void A_sleeve_sized_request_gets_the_scan_not_the_thumbnail() =>
        Assert.Equal("https://img/front-big.jpg", DiscogsJson.FrontCoverUrl(TwoImages, maxSize: 1200));

    /// <summary>Either size falls back to the other, not to nothing: a release with only one of the
    /// two still has a cover.</summary>
    [Theory]
    [InlineData(150, """{"images":[{"type":"primary","uri":"https://img/only-big.jpg"}]}""", "https://img/only-big.jpg")]
    [InlineData(1200, """{"images":[{"type":"primary","uri150":"https://img/only-thumb.jpg"}]}""", "https://img/only-thumb.jpg")]
    public void One_size_missing_falls_back_to_the_other(int maxSize, string json, string expected) =>
        Assert.Equal(expected, DiscogsJson.FrontCoverUrl(json, maxSize));

    [Fact]
    public void A_release_with_no_images_has_no_cover()
    {
        Assert.Null(DiscogsJson.FrontCoverUrl("""{"id":1}"""));
        Assert.Null(DiscogsJson.FrontCoverUrl("""{"images":[]}"""));
    }

    // ── durations ──

    [Theory]
    [InlineData("4:23", 263)]
    [InlineData("0:45", 45)]
    [InlineData("1:02:33", 3753)]   // a side-long track
    public void A_duration_is_minutes_and_seconds(string text, double expected) =>
        Assert.Equal(expected, DiscogsJson.Duration(text));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("?:??")]
    public void An_unreadable_duration_is_null(string? text) => Assert.Null(DiscogsJson.Duration(text));

    // ── the pressing's identity ──

    /// <summary>The catalogue number is on the LABEL, not the release, and a release names more than
    /// one label. The one that printed a number is the one on the spine.</summary>
    [Fact]
    public void The_catalogue_number_comes_off_the_label_that_has_one()
    {
        var facts = DiscogsJson.ParseReleaseFacts(
            """
            {
              "id": 9887127,
              "released": "2005-06-06",
              "labels": [
                { "name": "Universal", "catno": "none" },
                { "name": "A&M Records", "catno": "B0004341-02" }
              ],
              "genres": ["Hip Hop", "Electronic"],
              "styles": ["Pop Rap", "Synth-pop"]
            }
            """);

        Assert.Equal("B0004341-02", facts.Catno);
        Assert.Equal("A&M Records", facts.Label);
        Assert.Equal("2005-06-06", facts.Released);
        Assert.Equal(["Pop Rap", "Synth-pop"], facts.Styles);
        Assert.Equal(["Hip Hop", "Electronic"], facts.Genres);
    }

    /// <summary>Many records have no catalogue number at all, and Discogs writes the word "none" for
    /// them. Read literally, it would file every such record under one key.</summary>
    [Fact]
    public void None_is_not_a_catalogue_number()
    {
        var facts = DiscogsJson.ParseReleaseFacts(
            """{"id":1,"labels":[{"name":"Self-released","catno":"none"}]}""");

        Assert.Null(facts.Catno);
        Assert.Equal("Self-released", facts.Label);
    }

    /// <summary>Half of Discogs' releases are dated by year alone, and the year is a number there
    /// where "released" is a string. Either way the page has something to print.</summary>
    [Fact]
    public void A_release_dated_by_year_alone_still_has_a_date()
    {
        Assert.Equal("2013", DiscogsJson.ParseReleaseFacts("""{"id":1,"year":2013}""").Released);
        Assert.Null(DiscogsJson.ParseReleaseFacts("""{"id":1}""").Released);
    }

    [Fact]
    public void A_release_with_no_labels_reads_back_empty_rather_than_failing()
    {
        var facts = DiscogsJson.ParseReleaseFacts("""{"id":1,"artists":[{"name":"Kavinsky"}]}""");

        Assert.Null(facts.Catno);
        Assert.Null(facts.Label);
        Assert.Equal("Kavinsky", facts.Artist);
        Assert.Empty(facts.Styles);
        Assert.Empty(facts.Genres);
    }

    /// <summary>Discogs files a record under both a genre and a style, and the genre tag on the file is
    /// a genre: "Hip Hop", not "Pop Rap". It goes on the app's own shelf for that genre.</summary>
    [Fact]
    public void The_genre_tag_comes_from_the_genre_field()
    {
        var detail = DiscogsJson.ParseRelease(
            """
            {
              "id": 1, "title": "X", "tracklist": [],
              "genres": ["Hip Hop", "Electronic"],
              "styles": ["Pop Rap", "Synth-pop"]
            }
            """, "1");

        Assert.Equal("Hip-Hop / Rap", detail!.Genre);
    }

    /// <summary>Some releases are filed under a style and nothing else, and a style the app knows is a
    /// better answer than no genre at all.</summary>
    [Fact]
    public void A_style_stands_in_when_there_is_no_genre()
    {
        var detail = DiscogsJson.ParseRelease(
            """{"id":1,"title":"X","tracklist":[],"styles":["Post-Punk","Ska"]}""", "1");

        Assert.Equal("Punk", detail!.Genre);
    }

    /// <summary>The app recognises nothing on either side, so the record keeps the Discogs name and
    /// does not lose its genre entirely.</summary>
    [Fact]
    public void A_genre_the_app_does_not_know_is_kept_as_Discogs_wrote_it()
    {
        var detail = DiscogsJson.ParseRelease(
            """{"id":1,"title":"X","tracklist":[],"genres":["Non-Music"],"styles":["Shellac"]}""", "1");

        Assert.Equal("Non-Music", detail!.Genre);
    }

    // ── the release's pictures ──

    private const string Images = """
    {
      "id": 1, "title": "X", "tracklist": [],
      "images": [
        { "type": "primary",   "uri": "https://i.discogs.com/front.jpg",  "uri150": "https://i.discogs.com/front-150.jpg" },
        { "type": "secondary", "uri": "https://i.discogs.com/labelA.jpg", "uri150": "https://i.discogs.com/labelA-150.jpg" },
        { "type": "secondary", "uri": "https://i.discogs.com/labelB.jpg", "uri150": "https://i.discogs.com/labelB-150.jpg" }
      ]
    }
    """;

    /// <summary>The front cover is the sleeve and is read elsewhere. Here the code wants all the other
    /// images, which is where a photograph of the disc is.</summary>
    [Fact]
    public void The_other_images_are_every_picture_except_the_front()
    {
        var others = DiscogsJson.OtherImages(Images);

        Assert.Equal(2, others.Count);
        Assert.Equal("https://i.discogs.com/labelA.jpg", others[0].FullUrl);
        Assert.Equal("https://i.discogs.com/labelB.jpg", others[1].FullUrl);
    }

    /// <summary>Both sizes, each for its own use: the thumbnail is checked to decide whether the
    /// picture shows a disc, and the upload is fetched only for the one that does.</summary>
    [Fact]
    public void Each_picture_carries_the_thumbnail_and_the_upload()
    {
        var others = DiscogsJson.OtherImages(Images);

        Assert.Equal("https://i.discogs.com/labelA-150.jpg", others[0].ProbeUrl);
        Assert.Equal("https://i.discogs.com/labelA.jpg", others[0].FullUrl);
    }

    /// <summary>One size is enough. A picture offered only as the upload is still a picture, and
    /// dropping it would lose the disc on every release that has no Discogs thumbnail.</summary>
    [Fact]
    public void A_picture_with_only_one_size_stands_in_for_both()
    {
        var others = DiscogsJson.OtherImages("""
        {"id":1,"title":"X","tracklist":[],
         "images":[{"type":"secondary","uri":"https://i.discogs.com/only.jpg"}]}
        """);

        Assert.Single(others);
        Assert.Equal("https://i.discogs.com/only.jpg", others[0].ProbeUrl);
        Assert.Equal("https://i.discogs.com/only.jpg", others[0].FullUrl);
    }

    [Fact]
    public void A_release_with_no_pictures_has_no_other_pictures()
    {
        Assert.Empty(DiscogsJson.OtherImages("""{"id":1,"title":"X","tracklist":[]}"""));
    }

    /// <summary>The listing carries the count of those pictures, so the lookup window can show what a
    /// pressing has without a second throttled request per row. Same rule as OtherImages: the front
    /// cover is not one of them.</summary>
    [Fact]
    public void The_listing_says_how_many_pictures_the_pressing_has_besides_its_cover()
    {
        Assert.Equal(2, DiscogsJson.ParseRelease(Images, "1")!.Photos);
    }

    /// <summary>Nothing photographed is 0, not a guess. A release with only its front cover has no
    /// picture of the record in it, which is what the icon shows.</summary>
    [Theory]
    [InlineData("""{"id":1,"title":"X","tracklist":[]}""")]
    [InlineData("""{"id":1,"title":"X","tracklist":[],"images":[]}""")]
    [InlineData("""{"id":1,"title":"X","tracklist":[],"images":[{"type":"primary","uri":"https://i/front.jpg"}]}""")]
    public void A_pressing_photographed_sleeve_only_reports_no_pictures(string json)
    {
        Assert.Equal(0, DiscogsJson.ParseRelease(json, "1")!.Photos);
    }

    // ── the number Discogs files an act under, which is not part of their name ──

    /// <summary>Discogs adds a number after the name of the second act with the same name. It belongs
    /// to their database and not to the band, and a tag written with it names a band that does not
    /// exist.</summary>
    [Theory]
    [InlineData("The Biz (5)", "The Biz")]
    [InlineData("Benny Benassi Presents The Biz (5)", "Benny Benassi Presents The Biz")]
    [InlineData("Nirvana (2)", "Nirvana")]
    [InlineData("Aurora (13)", "Aurora")]
    public void The_filing_number_comes_off_a_name(string given, string wanted)
    {
        Assert.Equal(wanted, DiscogsJson.Named(given));
    }

    /// <summary>Nothing else is removed. The suffix is a number in brackets at the very end and only
    /// that: brackets in the middle of a name, words in brackets, and a name that IS brackets are all
    /// part of a real name and are returned whole.</summary>
    [Theory]
    [InlineData("Sunn O)))")]
    [InlineData("Prince (musician)")]
    [InlineData("Blink-182")]
    [InlineData("Front 242")]
    [InlineData("Apollo 440 (Live)")]
    [InlineData("(5)")]
    public void Everything_else_is_left_alone(string name)
    {
        Assert.Equal(name, DiscogsJson.Named(name));
    }

    /// <summary>The search names the artist only by joining it to the front of the title, so the
    /// number is removed there too: it is the same name from a different source.</summary>
    [Fact]
    public void A_search_result_loses_it_as_well()
    {
        var (artist, title) = DiscogsJson.SplitTitle("Benny Benassi Presents The Biz (5) - Satisfaction");
        Assert.Equal("Benny Benassi Presents The Biz", artist);
        Assert.Equal("Satisfaction", title);
    }

    /// <summary>A pressing's own artists have it removed when the listing is read, because that field
    /// goes into the tag.</summary>
    [Fact]
    public void A_pressings_artists_lose_it()
    {
        const string json = """
            {"id":1,"title":"Satisfaction","artists":[{"name":"The Biz (5)"},{"name":"Nirvana (2)"}],
             "tracklist":[]}
            """;
        Assert.Equal("The Biz, Nirvana", DiscogsJson.ParseRelease(json, "1")!.Artist);
    }
}

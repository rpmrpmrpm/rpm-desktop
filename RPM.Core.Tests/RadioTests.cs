using System.Net;
using System.Text;
using RPM.Core.Audio;
using RPM.Core.Radio;

namespace RPM.Core.Tests;

public class RadioDirectoryTests
{
    /// <summary>A directory that answers whatever it is told to, and remembers what it was asked.</summary>
    private sealed class FakeDirectory(params (HttpStatusCode Status, string Body)[] replies) : HttpMessageHandler
    {
        private int _at;

        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // AbsoluteUri, not ToString: ToString returns the unescaped form, which would hide
            // exactly the escaping bug this test is here to catch.
            Requests.Add(request.RequestUri!.AbsoluteUri);
            var (status, body) = replies[Math.Min(_at++, replies.Length - 1)];
            if (status >= HttpStatusCode.InternalServerError && body.Length == 0)
                throw new HttpRequestException("mirror down");
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private const string OneStation = """
        [{
          "stationuuid": "abc-123",
          "name": "  Radio Nova  ",
          "url": "http://vanity.example/live",
          "url_resolved": "http://cdn.example/live.mp3",
          "homepage": "http://nova.example",
          "favicon": "http://nova.example/logo.png",
          "tags": "jazz, Soul,jazz, ",
          "country": "France",
          "countrycode": "fr",
          "language": "french",
          "codec": "mp3",
          "bitrate": 128,
          "votes": 42,
          "clickcount": 900,
          "hls": 0,
          "lastcheckok": 1
        }]
        """;

    private static RadioBrowserClient Client(FakeDirectory directory) => new(new HttpClient(directory));

    [Fact]
    public async Task Reads_A_Station_The_Directory_Sent()
    {
        var station = (await Client(new FakeDirectory((HttpStatusCode.OK, OneStation)))
            .SearchAsync(new RadioQuery())).Single();

        Assert.Equal("abc-123", station.Uuid);
        Assert.Equal("Radio Nova", station.Name);          // trimmed
        Assert.Equal("France", station.Country);
        Assert.Equal("FR", station.CountryCode);           // upper-cased, which is what the filter uses
        Assert.Equal(128, station.BitrateKbps);
        Assert.Equal("MP3 128k", station.Quality);
        Assert.True(station.Reachable);
    }

    [Fact]
    public async Task The_Resolved_Url_Wins_Because_A_Vanity_Domain_Is_The_One_That_Lapses()
    {
        var station = (await Client(new FakeDirectory((HttpStatusCode.OK, OneStation)))
            .SearchAsync(new RadioQuery())).Single();

        Assert.Equal("http://cdn.example/live.mp3", station.StreamUrl);
    }

    [Fact]
    public void Tags_Arrive_As_One_String_And_Come_Back_As_A_Deduplicated_List()
    {
        Assert.Equal(["jazz", "soul"], RadioBrowserClient.SplitTags("jazz, Soul,jazz, "));
        Assert.Empty(RadioBrowserClient.SplitTags(null));
    }

    [Fact]
    public async Task A_Station_With_No_Url_Is_Dropped_Rather_Than_Listed_Unplayable()
    {
        const string Broken = """
            [{"stationuuid":"1","name":"No Stream","url":"","url_resolved":""},
             {"stationuuid":"","name":"No Id","url":"http://x.example/s"},
             {"stationuuid":"2","name":"Fine","url":"http://x.example/ok"}]
            """;

        var stations = await Client(new FakeDirectory((HttpStatusCode.OK, Broken))).SearchAsync(new RadioQuery());

        Assert.Equal("Fine", Assert.Single(stations).Name);
    }

    [Fact]
    public async Task The_Search_Asks_For_Working_Stations_Most_Played_First()
    {
        var directory = new FakeDirectory((HttpStatusCode.OK, "[]"));

        await Client(directory).SearchAsync(new RadioQuery
        {
            Name = "deep house",
            Tag = StationTag.Exactly("jazz"),
            CountryCode = "NL",
        });

        var asked = Assert.Single(directory.Requests);
        Assert.Contains("hidebroken=true", asked);
        Assert.Contains("order=clickcount", asked);
        Assert.Contains("reverse=true", asked);
        Assert.Contains("name=deep%20house", asked);      // escaped, not concatenated raw
        Assert.Contains("tag=jazz", asked);
        Assert.Contains("tagExact=true", asked);
        Assert.Contains("countrycode=NL", asked);
    }

    /// <summary>A tag asked for the way the directory's tags HOLD it. This is the only way one term
    /// reaches the three spellings the directory carries for the same thing.</summary>
    [Fact]
    public async Task A_Tag_Can_Be_Asked_For_As_Something_A_Stations_Tags_Hold()
    {
        var directory = new FakeDirectory((HttpStatusCode.OK, "[]"));

        await Client(directory).SearchAsync(new RadioQuery { Tag = StationTag.Holding("hop") });

        var asked = Assert.Single(directory.Requests);
        Assert.Contains("tag=hop", asked);
        Assert.Contains("tagExact=false", asked);
    }

    /// <summary>Every label on the menu has a tag to ask the directory for, because a label with none
    /// would come back with whatever the last search happened to return.</summary>
    [Fact]
    public void Every_Genre_On_The_Menu_Has_A_Tag_To_Ask_The_Directory_For()
    {
        foreach (var genre in RadioGenres.All)
            Assert.False(string.IsNullOrWhiteSpace(RadioGenres.Term(genre)), genre);

        Assert.Null(RadioGenres.Term("Polka"));
        Assert.Null(RadioGenres.Term(null));
    }

    [Fact]
    public async Task A_Dead_Mirror_Is_Walked_Past_Rather_Than_Failing_The_Search()
    {
        // The directory is a pool of volunteer mirrors, and one being down is normal, not an outage.
        var directory = new FakeDirectory(
            (HttpStatusCode.InternalServerError, ""),
            (HttpStatusCode.OK, OneStation));

        var stations = await Client(directory).SearchAsync(new RadioQuery());

        Assert.Single(stations);
        Assert.Equal(2, directory.Requests.Count);
        Assert.StartsWith("https://all.api.radio-browser.info/", directory.Requests[0]);
        Assert.NotEqual(new Uri(directory.Requests[0]).Host, new Uri(directory.Requests[1]).Host);
    }

    [Fact]
    public async Task The_Mirror_That_Answered_Is_Asked_First_Next_Time()
    {
        var directory = new FakeDirectory(
            (HttpStatusCode.InternalServerError, ""),
            (HttpStatusCode.OK, "[]"));
        var client = Client(directory);

        await client.SearchAsync(new RadioQuery());
        var winner = new Uri(directory.Requests[1]).Host;

        await client.SearchAsync(new RadioQuery());

        Assert.Equal(winner, new Uri(directory.Requests[2]).Host);
        Assert.Equal(3, directory.Requests.Count);       // no wasted call on the mirror known to be down
    }

    [Fact]
    public async Task Every_Mirror_Down_Is_Reported_As_Such()
    {
        var directory = new FakeDirectory((HttpStatusCode.InternalServerError, ""));

        await Assert.ThrowsAsync<RadioDirectoryUnavailableException>(
            () => Client(directory).SearchAsync(new RadioQuery()));
    }

    [Fact]
    public async Task Only_Tags_With_Real_Numbers_Behind_Them_Are_Offered()
    {
        // The directory holds tens of thousands of crowd-entered tags, nearly all of them typos.
        const string Tags = """
            [{"name":"jazz","stationcount":4000},
             {"name":"jaz","stationcount":2},
             {"name":"rock","stationcount":9000}]
            """;

        var tags = await Client(new FakeDirectory((HttpStatusCode.OK, Tags))).TagsAsync(minStations: 100);

        Assert.Equal(["rock", "jazz"], tags.Select(t => t.Name));   // biggest first
    }
}

public class RadioFavoritesTests
{
    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), "rpm-radio-tests", Guid.NewGuid().ToString("N"), "radio.json");

    /// <summary>A clock that moves one second on every reading, so each save has its own time.</summary>
    private sealed class Ticking : TimeProvider
    {
        private long _seconds = 1_700_000_000;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(_seconds++);
    }

    private static RadioStation Station(string uuid, string name = "Station") => new()
    {
        Uuid = uuid,
        Name = name,
        StreamUrl = $"http://example/{uuid}",
        Tags = ["jazz"],
        Codec = "MP3",
        BitrateKbps = 128,
    };

    [Fact]
    public void Saved_Stations_Survive_A_Restart()
    {
        var path = TempFile();
        new RadioFavorites(path, TimeProvider.System).Add(Station("a", "Radio A"));

        var reopened = new RadioFavorites(path, TimeProvider.System).Stations;

        var station = Assert.Single(reopened);
        Assert.Equal("Radio A", station.Name);
        // The whole station, not just its id: a preset has to keep playing after the directory has
        // pruned the entry, and it can only do that if it kept the URL.
        Assert.Equal("http://example/a", station.StreamUrl);
        Assert.Equal(["jazz"], station.Tags);
    }

    [Fact]
    public void Starring_Twice_Refreshes_The_Station_Instead_Of_Listing_It_Twice()
    {
        var favorites = new RadioFavorites(TempFile(), TimeProvider.System);
        favorites.Add(Station("a", "Old Name"));
        favorites.Add(Station("b"));

        favorites.Add(Station("a", "New Name"));

        Assert.Equal(["a", "b"], favorites.Stations.Select(s => s.Uuid));   // kept its place
        Assert.Equal("New Name", favorites.Stations[0].Name);
    }

    [Fact]
    public void The_Star_Toggles_And_Says_Which_Way_It_Went()
    {
        var favorites = new RadioFavorites(TempFile(), TimeProvider.System);
        var station = Station("a");

        Assert.True(favorites.Toggle(station));
        Assert.True(favorites.Contains("a"));
        Assert.False(favorites.Toggle(station));
        Assert.False(favorites.Contains("a"));
    }

    /// <summary>The order every device agrees on, since the saved stations are kept in step between
    /// devices: the order they were saved in.</summary>
    [Fact]
    public void Presets_Are_In_The_Order_They_Were_Saved()
    {
        // The real clock can give two saves the same millisecond, and then the order falls back to the ids.
        var favorites = new RadioFavorites(TempFile(), new Ticking());
        foreach (var id in new[] { "c", "a", "b" })
            favorites.Add(Station(id));

        Assert.Equal(["c", "a", "b"], favorites.Stations.Select(s => s.Uuid));
    }

    /// <summary>A file written before syncing existed has the list and no ledger: the list is kept,
    /// in its order, and all of it is sent on the first sync.</summary>
    [Fact]
    public void A_File_From_Before_The_Sync_Keeps_Its_List_And_Sends_All_Of_It()
    {
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            { "Stations": [
              { "Uuid": "b", "Name": "B", "StreamUrl": "http://example/b" },
              { "Uuid": "a", "Name": "A", "StreamUrl": "http://example/a" } ] }
            """);

        var favorites = new RadioFavorites(path, TimeProvider.System);

        Assert.Equal(["b", "a"], favorites.Stations.Select(s => s.Uuid));
        Assert.Equal(["b", "a"], favorites.ToSend(500).Keys);
    }

    [Fact]
    public void An_Unsave_Survives_A_Restart_As_A_Change_To_Send()
    {
        var path = TempFile();
        var favorites = new RadioFavorites(path, TimeProvider.System);
        favorites.Add(Station("a"));
        favorites.NoteSent(favorites.ToSend(500));
        favorites.Remove("a");

        var reopened = new RadioFavorites(path, TimeProvider.System);

        Assert.Empty(reopened.Stations);
        Assert.False(Assert.Single(reopened.ToSend(500)).Value.Saved);
    }

    [Fact]
    public void A_Corrupt_Presets_File_Costs_The_Presets_And_Nothing_Else()
    {
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ this is not json");

        var favorites = new RadioFavorites(path, TimeProvider.System);

        Assert.Empty(favorites.Stations);
        favorites.Add(Station("a"));                         // and it recovers on the next write
        Assert.Single(new RadioFavorites(path, TimeProvider.System).Stations);
    }
}

public class IcyStreamTests
{
    private const int MetaInt = 16;

    /// <summary>Builds what a station actually sends: audio, a length byte, a padded metadata block,
    /// audio, and so on.</summary>
    private static byte[] Broadcast(params object[] parts)
    {
        var bytes = new List<byte>();
        foreach (var part in parts)
        {
            if (part is byte[] audio)
            {
                bytes.AddRange(audio);
                continue;
            }

            var text = (string)part;
            if (text.Length == 0)
            {
                bytes.Add(0);                                // "unchanged", the common case
                continue;
            }

            var block = Encoding.UTF8.GetBytes(text);
            int blocks = (block.Length + 15) / 16;
            bytes.Add((byte)blocks);
            bytes.AddRange(block);
            bytes.AddRange(new byte[blocks * 16 - block.Length]);
        }
        return [.. bytes];
    }

    private static byte[] Audio(byte value, int count = MetaInt) => [.. Enumerable.Repeat(value, count)];

    [Fact]
    public void The_Audio_Comes_Out_With_The_Metadata_Taken_Off()
    {
        var raw = Broadcast(Audio(1), "StreamTitle='Herbie Hancock - Chameleon';", Audio(2), "");
        using var icy = new IcyStream(new MemoryStream(raw), MetaInt);

        var audio = new MemoryStream();
        icy.CopyTo(audio);

        // Exactly the audio, in order: one byte of metadata reaching a decoder is a click.
        Assert.Equal([.. Audio(1), .. Audio(2)], audio.ToArray());
    }

    [Fact]
    public void The_Station_Says_What_It_Is_Playing()
    {
        var raw = Broadcast(Audio(1), "StreamTitle='Herbie Hancock - Chameleon';StreamUrl='';", Audio(2), "");
        using var icy = new IcyStream(new MemoryStream(raw), MetaInt);
        var announced = new List<string>();
        icy.TitleChanged += announced.Add;

        icy.CopyTo(Stream.Null);

        Assert.Equal("Herbie Hancock - Chameleon", Assert.Single(announced));
        Assert.Equal("Herbie Hancock - Chameleon", icy.Title);
    }

    [Fact]
    public void The_Same_Title_Sent_Again_Is_Not_A_New_Track()
    {
        // Stations send the current title again on a timer. Announcing each one would restart every
        // readout that animates on a change.
        var raw = Broadcast(Audio(1), "StreamTitle='A - B';", Audio(2), "StreamTitle='A - B';",
                            Audio(3), "StreamTitle='C - D';", Audio(4), "");
        using var icy = new IcyStream(new MemoryStream(raw), MetaInt);
        var announced = new List<string>();
        icy.TitleChanged += announced.Add;

        icy.CopyTo(Stream.Null);

        Assert.Equal(["A - B", "C - D"], announced);
    }

    [Theory]
    [InlineData("StreamTitle='Simple';", "Simple")]
    [InlineData("StreamTitle='Guns N' Roses - Patience';StreamUrl='x';", "Guns N' Roses - Patience")]
    [InlineData("StreamTitle='No terminator", "No terminator")]
    [InlineData("StreamUrl='http://x';", null)]
    [InlineData("StreamTitle='';", null)]
    public void Titles_Are_Read_Out_Of_The_Block_Apostrophes_And_All(string block, string? expected)
    {
        Assert.Equal(expected, IcyStream.ParseTitle(block));
    }

    [Fact]
    public void A_Short_Read_Does_Not_Put_The_Next_Block_Out_Of_Step()
    {
        // Sockets return whatever has arrived. Reading past a boundary by one byte puts every block
        // after it out of step, and the text starts being decoded as audio.
        var raw = Broadcast(Audio(1), "StreamTitle='A';", Audio(2), "");
        using var icy = new IcyStream(new MemoryStream(raw), MetaInt);

        var audio = new MemoryStream();
        var chunk = new byte[7];                              // deliberately not a divisor of MetaInt
        int n;
        while ((n = icy.Read(chunk, 0, chunk.Length)) > 0)
            audio.Write(chunk, 0, n);

        Assert.Equal([.. Audio(1), .. Audio(2)], audio.ToArray());
        Assert.Equal("A", icy.Title);
    }
}

public class IcyTitleTests
{
    [Theory]
    [InlineData("Herbie Hancock - Chameleon", "Herbie Hancock", "Chameleon")]
    [InlineData("  Air   -   La Femme d'Argent  ", "Air", "La Femme d'Argent")]
    [InlineData("Daft Punk – Around the World", "Daft Punk", "Around the World")]    // en dash
    public void Artist_And_Track_Come_Apart_At_The_Separator(string announced, string artist, string title)
    {
        Assert.Equal((artist, title), IcyTitle.Split(announced));
    }

    [Fact]
    public void A_Hyphen_Inside_A_Name_Is_Not_A_Separator()
    {
        // Splitting on any hyphen turns Jay-Z into an artist called "Jay". The separator is a hyphen
        // with spaces around it, and nothing else.
        Assert.Equal(("Jay-Z", "99 Problems"), IcyTitle.Split("Jay-Z - 99 Problems"));
        Assert.Equal(("Wu-Tang Clan", "C.R.E.A.M."), IcyTitle.Split("Wu-Tang Clan - C.R.E.A.M."));
    }

    [Fact]
    public void Everything_After_The_First_Separator_Belongs_To_The_Track()
    {
        // The trailing part is nearly always a remix or a live tag, and it is part of the title.
        Assert.Equal(
            ("Blue Öyster Cult", "(Don't Fear) The Reaper - Live"),
            IcyTitle.Split("Blue Öyster Cult - (Don't Fear) The Reaper - Live"));
    }

    [Theory]
    [InlineData("Sublime Sunday Chill")]                 // the station announcing itself
    [InlineData("- Live from Paris")]                    // a separator with nothing before it
    [InlineData("Now playing -")]                        // and nothing after it
    public void What_Is_Not_A_Pair_Is_Left_Whole(string announced)
    {
        var (artist, title) = IcyTitle.Split(announced);

        Assert.Null(artist);
        Assert.Equal(announced.Trim(), title);
    }

    [Fact]
    public void Nothing_Announced_Is_Nothing_Shown()
    {
        Assert.Equal((null, null), IcyTitle.Split(null));
        Assert.Equal((null, null), IcyTitle.Split("   "));
    }
}

public class StreamPlaylistTests
{
    [Theory]
    [InlineData("audio/mpeg", "[playlist]", false)]           // it says audio; believe it
    [InlineData("audio/x-mpegurl", "\xff\xfb", true)]         // it says playlist; believe that too
    [InlineData("text/plain", "[playlist]\nFile1=http://x", true)]
    [InlineData("text/plain", "#EXTM3U\nhttp://x", true)]
    [InlineData(null, "ID3", false)]
    public void A_Playlist_Is_Told_From_Audio_By_Its_Type_Or_Failing_That_Its_First_Bytes(
        string? contentType, string head, bool expected)
    {
        var bytes = head.Select(c => (byte)c).ToArray();
        Assert.Equal(expected, StreamPlaylist.LooksLikePlaylist(contentType, bytes));
    }

    [Fact]
    public void The_First_Stream_In_A_Pls_Is_Followed()
    {
        const string Pls = """
            [playlist]
            NumberOfEntries=2
            File1=http://stream.example/live.mp3
            Title1=The Station
            File2=http://backup.example/live.mp3
            """;

        Assert.Equal("http://stream.example/live.mp3", StreamPlaylist.FirstUrl(Pls));
    }

    [Fact]
    public void An_M3u_Is_Read_Past_Its_Comments()
    {
        const string M3u = "#EXTM3U\n#EXTINF:-1,The Station\nhttps://stream.example/live\n";

        Assert.Equal("https://stream.example/live", StreamPlaylist.FirstUrl(M3u));
    }

    [Fact]
    public void A_Protocol_We_Cannot_Open_Is_Not_Handed_Back_As_If_We_Could()
    {
        Assert.Null(StreamPlaylist.FirstUrl("[playlist]\nFile1=mms://old.example/live\n"));
        Assert.Null(StreamPlaylist.FirstUrl(""));
    }
}

public class RadioStreamSourceTests
{
    private const int Channels = 2;
    private const int Block = 64;

    private static RadioStation Station() => new()
    {
        Uuid = "s1",
        Name = "Test Radio",
        StreamUrl = "http://example/live",
    };

    /// <summary>A station that plays <paramref name="frames"/> frames and then fails.</summary>
    private sealed class Burst(int frames, float value) : IPcmSource
    {
        private int _left = frames;

        public string Label => "burst";

        public int Read(float[] buffer, int frames)
        {
            int take = Math.Min(frames, _left);
            if (take <= 0) return 0;
            for (int i = 0; i < take * Channels; i++) buffer[i] = value;
            _left -= take;
            return take;
        }

        public void Dispose() => Disposed = true;

        public bool Disposed { get; private set; }
    }

    [Fact]
    public void A_Dropped_Station_Is_Reconnected_And_The_Music_Carries_On()
    {
        int opens = 0;
        using var radio = new RadioStreamSource(Station(), 44100, Channels,
            (_, _) => { opens++; return new Burst(Block, opens); });

        var buffer = new float[Block * Channels];
        Assert.Equal(Block, radio.Read(buffer, Block));
        Assert.Equal(1, buffer[0]);                       // the first connection
        Assert.Equal(Block, radio.Read(buffer, Block));
        Assert.Equal(2, buffer[0]);                       // it dropped, and came straight back

        Assert.Equal(2, opens);
    }

    [Fact]
    public void An_Unreachable_Station_Hands_Back_Silence_Rather_Than_Ending_The_Stream()
    {
        // Returning zero would tell the engine that the track finished, which shuts down the whole
        // player, for what is usually a ten-second outage.
        using var radio = new RadioStreamSource(Station(), 44100, Channels, (_, _) => null);

        var buffer = new float[Block * Channels];
        buffer[0] = 0.5f;

        Assert.Equal(Block, radio.Read(buffer, Block));
        Assert.Equal(0, buffer[0]);                       // a full block, and it is silence
        Assert.Equal(Block, radio.Read(buffer, Block));   // and it keeps trying rather than ending
    }

    [Fact]
    public void A_Station_That_Never_Comes_Back_Eventually_Stops_The_Player()
    {
        // Silence forever is worse than stopping: nothing on screen would ever show that the radio
        // is over.
        using var radio = new RadioStreamSource(Station(), 44100, Channels, (_, _) => null);
        var buffer = new float[Block * Channels];

        int reads = 0;
        while (radio.Read(buffer, Block) > 0 && reads < RadioStreamSource.MaxAttempts + 4)
            reads++;

        Assert.True(reads <= RadioStreamSource.MaxAttempts,
            $"gave up after {reads} tries, expected at most {RadioStreamSource.MaxAttempts}");
    }

    [Fact]
    public void Tuning_In_Is_Tried_At_Once_Rather_Than_After_A_Backoff()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var radio = new RadioStreamSource(Station(), 44100, Channels, (_, _) => new Burst(Block, 1));

        Assert.Equal(Block, radio.Read(new float[Block * Channels], Block));

        Assert.True(clock.ElapsedMilliseconds < 200, $"first connection waited {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void The_Readout_Follows_The_Station_And_Counts_What_Has_Played()
    {
        using var radio = new RadioStreamSource(Station(), 44100, Channels, (_, _) => new Burst(44100, 1));

        var now = radio.NowPlayingTrack;
        Assert.NotNull(now);
        // Before the station has announced anything: its own name is the title, so the header is never
        // blank while the radio is on. The name appears ONCE, with the line below left empty and not
        // carrying the same name twice.
        Assert.Equal("Test Radio", now!.Value.Title);
        Assert.Null(now.Value.Album);
        Assert.Equal(0, now.Value.DurationSeconds);      // radio does not end, and the scrubber reads that

        radio.Read(new float[22050 * Channels], 22050);

        Assert.Equal(0.5, radio.NowPlayingTrack!.Value.PositionSeconds, 3);
    }

    [Fact]
    public void The_Header_Gets_The_Announced_Artist_And_Track_In_The_Usual_Fields()
    {
        // The header shows a radio station through exactly the fields it shows a file through, so
        // nothing downstream needs a special case for the radio.
        var station = Station() with { Favicon = "http://example/logo.png" };
        Action<string>? announce = null;
        using var radio = new RadioStreamSource(station, 44100, Channels,
            (_, sink) => { announce = raw => sink(IcyFields.Read(raw)); return new Burst(Block, 1); });

        radio.Read(new float[Block * Channels], Block);
        announce!("Herbie Hancock - Chameleon");

        var now = radio.NowPlayingTrack!.Value;
        Assert.Equal("Herbie Hancock", now.Artist);
        Assert.Equal("Chameleon", now.Title);
        Assert.Equal("Test Radio", now.Album);
        Assert.Equal("http://example/logo.png", now.ArtUrl);   // the logo stands in for the sleeve
    }

    // ── the fields an encoder adds, removed on arrival (see IcyFields) ──

    /// <summary>The artist in front of the separator, the title in a field, and the schedule's ids
    /// after it: 102.7 KIIS FM's own line. The header gets the pair the station named.</summary>
    [Fact]
    public void A_Record_Announced_In_Fields_Reaches_The_Header_As_A_Pair()
    {
        Action<string>? announce = null;
        using var radio = new RadioStreamSource(Station(), 44100, Channels,
            (_, sink) => { announce = raw => sink(IcyFields.Read(raw)); return new Burst(Block, 1); });

        radio.Read(new float[Block * Channels], Block);
        announce!("Kehlani - text=\"Folded\" song_spot=\"M\" MediaBaseId=\"3133844\" " +
                  "amgTrackId=\"-1\" TPID=\"396193730\" length=\"00:03:57\" spotInstanceId=\"-1\"");

        Assert.Equal("Kehlani - Folded", radio.Title);      // the dial's line, and the ad marker's

        var now = radio.NowPlayingTrack!.Value;
        Assert.Equal("Kehlani", now.Artist);                // …and the header's two fields
        Assert.Equal("Folded", now.Title);
    }

    /// <summary>The station's own ident is written the same way and is not a record. Its slogan is not
    /// an artist, so nothing is put in the artist field.</summary>
    [Fact]
    public void An_Ident_Is_Not_Held_As_A_Record()
    {
        Action<string>? announce = null;
        using var radio = new RadioStreamSource(Station(), 44100, Channels,
            (_, sink) => { announce = raw => sink(IcyFields.Read(raw)); return new Burst(Block, 1); });

        radio.Read(new float[Block * Channels], Block);
        announce!("LA's #1 Hit Music Station - text=\"102.7 KIIS FM\" song_spot=\"T\" " +
                  "MediaBaseId=\"0\" length=\"00:00:00\"");

        var now = radio.NowPlayingTrack!.Value;
        Assert.Equal("102.7 KIIS FM", now.Title);
        Assert.Null(now.Artist);
    }

    /// <summary>Nothing named is the ordinary state of a dial between records, and it has to read that
    /// way: the station's name once, not on both lines of the header.</summary>
    [Fact]
    public void With_Nothing_Named_The_Station_Is_Printed_Once()
    {
        Action<string>? announce = null;
        using var radio = new RadioStreamSource(Station(), 44100, Channels,
            (_, sink) => { announce = raw => sink(IcyFields.Read(raw)); return new Burst(Block, 1); });

        radio.Read(new float[Block * Channels], Block);
        announce!("- text=\"Spot Block End\" amgTrackId=\"9876543\" length=\"00:00:00\"");

        var now = radio.NowPlayingTrack!.Value;
        Assert.Equal("Test Radio", now.Title);
        Assert.Null(now.Artist);
        Assert.Null(now.Album);            // the same name on the line below is a readout looking broken
    }

    /// <summary>…and a record puts it back, because then the two lines say different things.</summary>
    [Fact]
    public void A_Record_Puts_The_Station_Back_On_The_Line_Below()
    {
        Action<string>? announce = null;
        using var radio = new RadioStreamSource(Station(), 44100, Channels,
            (_, sink) => { announce = raw => sink(IcyFields.Read(raw)); return new Burst(Block, 1); });

        radio.Read(new float[Block * Channels], Block);
        announce!("Kehlani - text=\"Folded\" song_spot=\"M\" length=\"00:03:57\"");

        var now = radio.NowPlayingTrack!.Value;
        Assert.Equal("Folded", now.Title);
        Assert.Equal("Kehlani", now.Artist);
        Assert.Equal("Test Radio", now.Album);
    }

    /// <summary>An ident that says only the station's own name has said nothing the header does not
    /// already print.</summary>
    [Fact]
    public void An_Ident_That_Is_Only_The_Stations_Name_Is_Held_As_Nothing()
    {
        Action<string>? announce = null;
        using var radio = new RadioStreamSource(Station(), 44100, Channels,
            (_, sink) => { announce = raw => sink(IcyFields.Read(raw)); return new Burst(Block, 1); });

        radio.Read(new float[Block * Channels], Block);
        announce!("Kehlani - text=\"Folded\" song_spot=\"M\" length=\"00:03:57\"");
        announce!("The Sound Of The City - text=\"Test Radio\" song_spot=\"T\" length=\"00:00:00\"");

        Assert.Null(radio.Title);          // not the record before it, and not the station's own name

        var now = radio.NowPlayingTrack!.Value;
        Assert.Equal("Test Radio", now.Title);
        Assert.Null(now.Album);
    }

    /// <summary>A station that states which half is which is not one whose order is guessed. The
    /// collection recognising the title as an artist must not turn a stated pair round.</summary>
    [Fact]
    public void A_Stated_Pair_Is_Not_Turned_Round_By_The_Collection()
    {
        Action<string>? announce = null;
        using var radio = new RadioStreamSource(Station(), 44100, Channels,
            (_, sink) => { announce = raw => sink(IcyFields.Read(raw)); return new Burst(Block, 1); })
        {
            // A collection that knows a band called "Folded" and has never heard of Kehlani: exactly
            // the evidence IcyOrders would use to turn the pair round.
            Orders = new IcyOrders(name => string.Equals(name, "Folded", StringComparison.Ordinal)),
        };

        radio.Read(new float[Block * Channels], Block);
        announce!("Kehlani - text=\"Folded\" song_spot=\"M\" length=\"00:03:57\"");

        var now = radio.NowPlayingTrack!.Value;
        Assert.Equal("Kehlani", now.Artist);
        Assert.Equal("Folded", now.Title);
    }

    /// <summary>A line from 102.7 KIIS FM. A break ending is not an announcement: nothing is held,
    /// and the header shows the station.</summary>
    [Fact]
    public void A_Break_Ending_Is_Held_As_Nothing_At_All()
    {
        Action<string>? announce = null;
        using var radio = new RadioStreamSource(Station(), 44100, Channels,
            (_, sink) => { announce = raw => sink(IcyFields.Read(raw)); return new Burst(Block, 1); });

        radio.Read(new float[Block * Channels], Block);
        announce!("Herbie Hancock - Chameleon");
        announce!("- text=\"Spot Block End\" amgTrackId=\"9876543\" length=\"00:00:00\"");

        Assert.Null(radio.Title);                             // not the marker, and not the last record

        var now = radio.NowPlayingTrack!.Value;
        Assert.Equal("Test Radio", now.Title);                // the station, where a record's name goes
        Assert.Null(now.Artist);
    }

    /// <summary>A break RUNNING is held, because the dial says what it is, but it is still not a
    /// title, so the header names the station and does not print the marker.</summary>
    [Fact]
    public void A_Break_Running_Is_Said_On_The_Dial_And_Not_In_The_Header()
    {
        Action<string>? announce = null;
        using var radio = new RadioStreamSource(Station(), 44100, Channels,
            (_, sink) => { announce = raw => sink(IcyFields.Read(raw)); return new Burst(Block, 1); });

        radio.Read(new float[Block * Channels], Block);
        announce!("- text=\"Spot Block Start\" amgTrackId=\"9876543\" length=\"00:00:00\"");

        Assert.True(RadioAds.IsAdBreak(radio.Title));         // what the readout reads: see RadioModel
        Assert.Equal("Test Radio", radio.NowPlayingTrack!.Value.Title);
    }

    [Fact]
    public async Task Stopping_Mid_Reconnect_Does_Not_Wait_Out_The_Backoff()
    {
        var radio = new RadioStreamSource(Station(), 44100, Channels, (_, _) => null);
        var buffer = new float[Block * Channels];
        radio.Read(buffer, Block);                       // first try fails; the next read pays the backoff

        var reading = Task.Run(() => radio.Read(buffer, Block));
        Thread.Sleep(50);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        radio.Dispose();

        Assert.True(await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(2))) == reading,
                    "a read stayed parked after Stop");
        Assert.True(clock.ElapsedMilliseconds < 500, $"Stop took {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void The_Decoder_Is_Let_Go_With_The_Station()
    {
        Burst? opened = null;
        var radio = new RadioStreamSource(Station(), 44100, Channels, (_, _) => opened = new Burst(44100, 1));
        radio.Read(new float[Block * Channels], Block);

        radio.Dispose();

        Assert.True(opened!.Disposed);
    }
}

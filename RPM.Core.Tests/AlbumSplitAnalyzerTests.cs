using RPM.Core.Identify;
using RPM.Core.Net;
using RPM.Core.Splitting;

namespace RPM.Core.Tests;

public class AlbumSplitAnalyzerTests : IDisposable
{
    private readonly string _root;

    public AlbumSplitAnalyzerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mdtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Touch(string relative, string content = "x")
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Envelope of 3 x 100s tracks separated by 2s silences (50ms windows, 306s total).</summary>
    private static float[] ThreeTrackEnvelope()
    {
        var env = new float[(int)(306 / 0.05)];
        Array.Fill(env, -12f);
        foreach (double gapStart in new[] { 100.0, 202.0 })
            for (int w = (int)(gapStart / 0.05); w < (int)((gapStart + 2) / 0.05); w++)
                env[w] = -90f;
        return env;
    }

    private static AlbumSplitAnalyzer Analyzer(
        ISegmentIdentificationBackend? backend,
        Func<string, CancellationToken, Task<ReleaseInfo?>>? releaseLookup = null,
        Func<string, double, double, (string, double)>? sliceFp = null) => new(backend, null)
    {
        SilenceOptions = new SilenceDetectorOptions { MinTrackSeconds = 30 },
        EnvelopeReader = _ => new EnvelopeScan(ThreeTrackEnvelope(), 0.05, 306),
        SliceFingerprinter = sliceFp ?? ((_, start, _) => ($"fp@{start:F0}", 60)),
        // The tests state their listings as plain answers; each is handed over as answered.
        ReleaseLookup = async (id, ct) => Asked.Answer(
            await (releaseLookup ?? ((_, _) => Task.FromResult<ReleaseInfo?>(null)))(id, ct)),
    };

    private sealed class SegmentBackend(Func<FingerprintQuery, SegmentIdentification?> respond) : ISegmentIdentificationBackend
    {
        public int Calls;
        public List<FingerprintQuery> Queries { get; } = new();
        public Task<IdentificationResult?> IdentifyAsync(FingerprintQuery query, CancellationToken ct = default) =>
            Task.FromResult<IdentificationResult?>(null);
        public Task<SegmentIdentification?> IdentifySegmentAsync(FingerprintQuery query, CancellationToken ct = default)
        {
            Calls++;
            Queries.Add(query);
            return Task.FromResult(respond(query));
        }
    }

    // ── Recognize selected: the basic layers, then the advanced one ──

    /// <summary>The basic layers, answering every lookup with one fixed result.</summary>
    private sealed class BasicLayers(IdentificationResult? answer) : ISegmentIdentificationBackend
    {
        public int Calls;
        public Task<IdentificationResult?> IdentifyAsync(FingerprintQuery query, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(answer);
        }
        public Task<SegmentIdentification?> IdentifySegmentAsync(FingerprintQuery query, CancellationToken ct = default) =>
            Task.FromResult<SegmentIdentification?>(null);
    }

    /// <summary>The advanced layer, answering every clip with one fixed result.</summary>
    private sealed class AdvancedLayer(IdentificationResult? answer) : IClipIdentificationBackend
    {
        public int Calls;
        public Task<IdentificationResult?> IdentifyAsync(FingerprintQuery query, CancellationToken ct = default) =>
            Task.FromResult<IdentificationResult?>(null);
        public Task<ClipAnswer> IdentifyClipAsync(FingerprintQuery query, byte[] clipMp3, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<ClipAnswer>(answer is null
                ? new ClipAnswer.AudDNotIdentified()
                : new ClipAnswer.AudDIdentified(answer));
        }
    }

    private static AlbumSplitAnalyzer Recognizer(BasicLayers basic, AdvancedLayer? advanced, List<(double Start, double Length)>? clips = null) =>
        new(basic, advanced)
        {
            SliceFingerprinter = (_, start, _) => ($"fp@{start:F0}", 60),
            ClipCutter = (_, start, length) =>
            {
                clips?.Add((start, length));
                return [1, 2, 3];
            },
        };

    private static IdentificationResult Answer(string title, double score, string source) =>
        new(title, "Artist", null, 0, score, source);

    [Fact]
    public async Task A_track_the_basic_layers_name_never_goes_to_the_advanced_layer()
    {
        var basic = new BasicLayers(Answer("Blue Monday", 0.9, "acoustid"));
        var advanced = new AdvancedLayer(Answer("Something else", 0.9, "audd"));

        var result = await Recognizer(basic, advanced).RecognizeTrackAsync("side.flac", 200, 300, reachAdvanced: true, default);

        Assert.Equal("Blue Monday", result?.Title);
        Assert.Equal(0, advanced.Calls);
    }

    [Fact]
    public async Task A_track_the_basic_layers_miss_goes_to_the_advanced_layer_with_a_clip_from_inside_it()
    {
        var basic = new BasicLayers(null);
        var advanced = new AdvancedLayer(Answer("Blue Monday", 0.9, "audd"));
        var clips = new List<(double Start, double Length)>();

        var result = await Recognizer(basic, advanced, clips).RecognizeTrackAsync("side.flac", 200, 300, reachAdvanced: true, default);

        Assert.Equal("Blue Monday", result?.Title);
        Assert.Equal(1, basic.Calls);
        Assert.Equal(1, advanced.Calls);
        // The track's own place in the recording, not the start of the file.
        Assert.Equal((200.0, 300.0), Assert.Single(clips));
    }

    [Fact]
    public async Task A_weak_basic_match_counts_as_a_miss()
    {
        var basic = new BasicLayers(Answer("Probably not", 0.3, "acoustid"));
        var advanced = new AdvancedLayer(Answer("Blue Monday", 0.9, "audd"));

        var result = await Recognizer(basic, advanced).RecognizeTrackAsync("side.flac", 0, 300, reachAdvanced: true, default);

        Assert.Equal("Blue Monday", result?.Title);
    }

    [Fact]
    public async Task A_run_that_may_not_reach_the_advanced_layer_stops_at_the_basic_layers()
    {
        var basic = new BasicLayers(null);
        var advanced = new AdvancedLayer(Answer("Blue Monday", 0.9, "audd"));

        var result = await Recognizer(basic, advanced).RecognizeTrackAsync("side.flac", 0, 300, reachAdvanced: false, default);

        Assert.Null(result);
        Assert.Equal(0, advanced.Calls);
    }

    [Fact]
    public async Task Without_an_advanced_layer_there_is_nothing_to_reach()
    {
        var analyzer = Recognizer(new BasicLayers(null), null);

        Assert.False(analyzer.CanReachAdvanced);
        Assert.Null(await analyzer.RecognizeTrackAsync("side.flac", 0, 300, reachAdvanced: true, default));
    }

    [Fact]
    public void The_clip_starts_a_third_of_the_way_into_the_track()
    {
        Assert.Equal(300, ClipCutter.ClipStart(200, 300), 3);
    }

    [Fact]
    public void A_short_track_is_clipped_from_its_own_start()
    {
        Assert.Equal(200, ClipCutter.ClipStart(200, 10), 3);
    }

    [Fact]
    public void A_clip_stays_inside_the_track()
    {
        // 20 s track: a third in is 6.7 s, but the 15 s clip must end a second before the track does,
        // so it starts 4 s in.
        Assert.Equal(104, ClipCutter.ClipStart(100, 20), 3);
    }

    private AlbumFileCandidate Candidate(string? cue = null) =>
        new(Path.Combine(_root, "album.mp3"), 306, cue, AlbumFileHint.LongDuration);

    [Fact]
    public async Task Offline_uses_silence_when_no_cue()
    {
        var analysis = await Analyzer(null).AnalyzeOfflineAsync(Candidate());

        Assert.Equal(3, analysis.Plan.Tracks.Count);
        Assert.All(analysis.Plan.Tracks, t => Assert.Equal(BoundarySource.Silence, t.Source));
        Assert.InRange(analysis.Plan.Tracks[1].StartSeconds, 100, 102.5);
    }

    [Fact]
    public async Task Offline_cue_wins_over_silence()
    {
        string cue = Touch("album.cue", """
            TITLE "Cue Album"
            FILE "album.mp3" MP3
              TRACK 01 AUDIO
                TITLE "First"
                INDEX 01 00:00:00
              TRACK 02 AUDIO
                TITLE "Second"
                INDEX 01 02:30:00
            """);

        var analysis = await Analyzer(null).AnalyzeOfflineAsync(Candidate(cue));

        Assert.Equal(2, analysis.Plan.Tracks.Count);
        Assert.All(analysis.Plan.Tracks, t => Assert.Equal(BoundarySource.CueSheet, t.Source));
        Assert.Equal("Cue Album", analysis.Plan.Album);
        Assert.Equal("First", analysis.Plan.Tracks[0].Title);
        Assert.Equal(150, analysis.Plan.Tracks[1].StartSeconds, 1);
    }

    [Fact]
    public async Task Identify_votes_release_and_aligns()
    {
        var releases = new[] { new ReleaseRef("REL-1", "The Album", 0, 3) };
        // Matched recording titles are the disc's own titles, and that is what picks the disc.
        var backend = new SegmentBackend(q => new SegmentIdentification(
            "rec", "Two", "The Band", 0.9, releases));
        var release = new ReleaseInfo("REL-1", "The Album", "The Band", 1997, new[]
        {
            (IReadOnlyList<ExpectedTrack>)new[]
            {
                new ExpectedTrack("One", "The Band", 101),
                new ExpectedTrack("Two", "The Band", 102),
                new ExpectedTrack("Three", "The Band", 103),
            },
        });
        var analyzer = Analyzer(backend, (id, _) =>
            Task.FromResult<ReleaseInfo?>(id == "REL-1" ? release : null));

        var offline = await analyzer.AnalyzeOfflineAsync(Candidate());
        var identified = await analyzer.IdentifyAsync(offline);

        Assert.Equal("The Album", identified.Plan.Album);
        Assert.Equal("REL-1", identified.Plan.ReleaseMbid);
        Assert.Equal(1997u, identified.Plan.Year);
        Assert.Equal(3, identified.Plan.Tracks.Count);
        Assert.Equal("One", identified.Plan.Tracks[0].Title);
        Assert.Equal("Two", identified.Plan.Tracks[1].Title);
        // Boundaries stay on detected silences (100-102 / 202-204).
        Assert.InRange(identified.Plan.Tracks[1].StartSeconds, 100, 102.5);
        Assert.InRange(identified.Plan.Tracks[2].StartSeconds, 202, 204.5);
        Assert.Equal(3, backend.Calls);
        // AcoustID gates on duration: the query must claim the FULL segment length, not the
        // fingerprinted slice's (~100s segments here).
        Assert.All(backend.Queries, q => Assert.InRange(q.DurationSeconds, 95, 110));
    }

    [Fact]
    public async Task Identify_without_consensus_keeps_boundaries_and_titles_matched_segments()
    {
        // Every segment matches a different release → no album vote; titles still adopted.
        int n = 0;
        var backend = new SegmentBackend(_ =>
        {
            n++;
            return new SegmentIdentification("rec" + n, $"Song {n}", "Artist", 0.8,
                new[] { new ReleaseRef($"REL-{n}", null, 0, 0) });
        });
        var analyzer = Analyzer(backend);

        var offline = await analyzer.AnalyzeOfflineAsync(Candidate());
        var identified = await analyzer.IdentifyAsync(offline);

        Assert.Null(identified.Plan.Album);
        Assert.Equal(3, identified.Plan.Tracks.Count);
        Assert.All(identified.Plan.Tracks, t => Assert.Equal(BoundarySource.Silence, t.Source));
        Assert.Equal("Song 1", identified.Plan.Tracks[0].Title);
        Assert.InRange(offline.Plan.Tracks[1].StartSeconds, identified.Plan.Tracks[1].StartSeconds - 0.001,
            identified.Plan.Tracks[1].StartSeconds + 0.001);
    }

    [Fact]
    public async Task Identify_ignores_low_score_matches()
    {
        var backend = new SegmentBackend(_ => new SegmentIdentification(
            "rec", "Noise", "Nobody", 0.2, new[] { new ReleaseRef("REL-X", null, 0, 0) }));
        var analyzer = Analyzer(backend);

        var offline = await analyzer.AnalyzeOfflineAsync(Candidate());
        var identified = await analyzer.IdentifyAsync(offline);

        Assert.Null(identified.Plan.Tracks[0].Title);
        Assert.Null(identified.Plan.Album);
    }

    [Fact]
    public async Task Identify_swallows_per_segment_failures()
    {
        int calls = 0;
        var backend = new SegmentBackend(_ =>
        {
            if (++calls == 1) throw new InvalidOperationException("network blip");
            return new SegmentIdentification("rec", "Ok", "A", 0.9,
                new[] { new ReleaseRef("REL-1", null, 0, 0) });
        });
        var analyzer = Analyzer(backend, (_, _) => Task.FromResult<ReleaseInfo?>(null));

        var offline = await analyzer.AnalyzeOfflineAsync(Candidate());
        var identified = await analyzer.IdentifyAsync(offline);

        // Two segments still matched; failure didn't abort.
        Assert.Equal("Ok", identified.Plan.Tracks[1].Title);
    }

    [Fact]
    public async Task Identify_cancellation_propagates()
    {
        var backend = new SegmentBackend(_ => new SegmentIdentification("r", "T", "A", 0.9,
            new[] { new ReleaseRef("REL-1", null, 0, 0) }));
        var analyzer = Analyzer(backend);
        var offline = await analyzer.AnalyzeOfflineAsync(Candidate());

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            analyzer.IdentifyAsync(offline, ct: cts.Token));
    }

    [Fact]
    public async Task Identify_without_backend_is_passthrough()
    {
        var analyzer = Analyzer(null);
        var offline = await analyzer.AnalyzeOfflineAsync(Candidate());

        var identified = await analyzer.IdentifyAsync(offline);

        Assert.Same(offline, identified);
        Assert.False(analyzer.CanIdentify);
    }

    [Fact]
    public async Task Identify_picks_disc_by_matched_titles_not_duration()
    {
        // Both discs are the same length. Only the matched segment titles say disc 2.
        int n = 0;
        var backend = new SegmentBackend(_ =>
        {
            n++;
            return new SegmentIdentification("rec" + n, $"D2T{n}", "Y", 0.9,
                new[] { new ReleaseRef("REL-2CD", "Big Set", 0, 3) });
        });
        var release = new ReleaseInfo("REL-2CD", "Big Set", "Various", 1995, new[]
        {
            (IReadOnlyList<ExpectedTrack>)new[]
            {
                new ExpectedTrack("D1T1", "X", 101),
                new ExpectedTrack("D1T2", "X", 102),
                new ExpectedTrack("D1T3", "X", 103),
            },
            new[]
            {
                new ExpectedTrack("D2T1", "Y", 101),
                new ExpectedTrack("D2T2", "Y", 102),
                new ExpectedTrack("D2T3", "Y", 103),
            },
        });
        var analyzer = Analyzer(backend, (_, _) => Task.FromResult<ReleaseInfo?>(release));

        var offline = await analyzer.AnalyzeOfflineAsync(Candidate());
        var identified = await analyzer.IdentifyAsync(offline);

        Assert.Equal("Big Set", identified.Plan.Album);
        Assert.Equal(2u, identified.Plan.DiscNumber);
        Assert.Equal(2u, identified.Plan.DiscCount);
        Assert.Equal("D2T1", identified.Plan.Tracks[0].Title);
    }

    [Fact]
    public async Task Identify_adopts_titles_positionally_when_disc_has_no_lengths()
    {
        // MB knows the disc's track list but no durations (real Time Life case): detected
        // segment count == disc track count → titles map onto the silence boundaries.
        int n = 0;
        var backend = new SegmentBackend(_ =>
        {
            n++;
            return new SegmentIdentification("rec" + n, $"Song {n}", "Y", 0.9,
                new[] { new ReleaseRef("REL-1", "The Album", 0, 3) });
        });
        var release = new ReleaseInfo("REL-1", "The Album", "Various", 1995, new[]
        {
            (IReadOnlyList<ExpectedTrack>)new[]
            {
                new ExpectedTrack("Song 1", "Y", null),
                new ExpectedTrack("Song 2", "Y", null),
                new ExpectedTrack("Song 3", "Y", null),
            },
        });
        var analyzer = Analyzer(backend, (_, _) => Task.FromResult<ReleaseInfo?>(release));

        var offline = await analyzer.AnalyzeOfflineAsync(Candidate());
        var identified = await analyzer.IdentifyAsync(offline);

        Assert.Equal("The Album", identified.Plan.Album);
        Assert.Equal(3, identified.Plan.Tracks.Count);
        Assert.Equal("Song 2", identified.Plan.Tracks[1].Title);
        // Boundaries untouched: still the detected silences.
        Assert.All(identified.Plan.Tracks, t => Assert.Equal(BoundarySource.Silence, t.Source));
        Assert.InRange(identified.Plan.Tracks[1].StartSeconds, 100, 102.5);
    }

    [Fact]
    public void TryMapToDisc_merges_false_split_between_anchors()
    {
        // 4 detected segments, 3 disc tracks: segments 1+2 are one song falsely split at a
        // quiet passage. Anchors: segment 0 = "One", segment 3 = "Three".
        var segments = new[]
        {
            new SplitTrack(0, 100, null, null, 1, 0.6, BoundarySource.Silence),
            new SplitTrack(100, 160, null, null, 2, 0.6, BoundarySource.Silence),
            new SplitTrack(160, 200, null, null, 3, 0.6, BoundarySource.Silence),
            new SplitTrack(200, 306, null, null, 4, 0.6, BoundarySource.Silence),
        };
        var disc = new[]
        {
            new ExpectedTrack("One", "A", null),
            new ExpectedTrack("Two", "A", null),
            new ExpectedTrack("Three", "A", null),
        };
        var matched = new Dictionary<int, string> { [0] = "One", [3] = "Three" };

        var mapped = AlbumSplitAnalyzer.TryMapToDisc(segments, disc, matched)!;

        Assert.Equal(3, mapped.Count);
        Assert.Equal("Two", mapped[1].Title);
        Assert.Equal(100, mapped[1].StartSeconds, 3);
        Assert.Equal(200, mapped[1].EndSeconds, 3);   // 1+2 merged
        Assert.Equal("Three", mapped[2].Title);
        Assert.Equal(new uint[] { 1, 2, 3 }, mapped.Select(t => t.TrackNumber).ToArray());
        Assert.True(mapped[0].Confidence >= 0.85);    // anchor
        Assert.True(mapped[1].Confidence >= 0.7);     // adopted
    }

    [Fact]
    public void TryMapToDisc_rejects_missing_boundaries_and_missing_anchors()
    {
        var two = new[]
        {
            new SplitTrack(0, 150, null, null, 1, 0.6, BoundarySource.Silence),
            new SplitTrack(150, 306, null, null, 2, 0.6, BoundarySource.Silence),
        };
        var threeDisc = new[]
        {
            new ExpectedTrack("One", "A", null), new ExpectedTrack("Two", "A", null), new ExpectedTrack("Three", "A", null),
        };
        // Fewer segments than tracks → a boundary is missing; can't invent it without durations.
        Assert.Null(AlbumSplitAnalyzer.TryMapToDisc(two, threeDisc, new Dictionary<int, string> { [0] = "One" }));

        // Surplus segments but no anchors → merging would be guessing.
        var four = new[]
        {
            new SplitTrack(0, 80, null, null, 1, 0.6, BoundarySource.Silence),
            new SplitTrack(80, 160, null, null, 2, 0.6, BoundarySource.Silence),
            new SplitTrack(160, 240, null, null, 3, 0.6, BoundarySource.Silence),
            new SplitTrack(240, 306, null, null, 4, 0.6, BoundarySource.Silence),
        };
        Assert.Null(AlbumSplitAnalyzer.TryMapToDisc(four, threeDisc, new Dictionary<int, string>()));
    }

    [Fact]
    public void PickDiscByMatches_requires_title_overlap()
    {
        var disc1 = (IReadOnlyList<ExpectedTrack>)new[] { new ExpectedTrack("Alpha", null, 150), new ExpectedTrack("Beta", null, 150) };
        var disc2 = (IReadOnlyList<ExpectedTrack>)new[] { new ExpectedTrack("Gamma!", null, null), new ExpectedTrack("Delta", null, null) };

        // Punctuation/case-insensitive overlap picks disc 2 despite its missing lengths.
        var picked = AlbumSplitAnalyzer.PickDiscByMatches(new[] { disc1, disc2 }, new[] { "gamma", "DELTA" }, 306);
        Assert.Equal(2u, picked!.Value.Number);

        // No overlap → null (never falls back to duration-only guessing).
        Assert.Null(AlbumSplitAnalyzer.PickDiscByMatches(new[] { disc1, disc2 }, new[] { "Unrelated" }, 306));
    }

    [Fact]
    public void Segment_sampling_caps_lookups()
    {
        var all = AlbumSplitAnalyzer.PickSegments(8);
        Assert.Equal(Enumerable.Range(0, 8), all);

        var sampled = AlbumSplitAnalyzer.PickSegments(30);
        Assert.Equal(AlbumSplitAnalyzer.MaxSegmentLookups, sampled.Count);
        Assert.Contains(0, sampled);
        Assert.Contains(29, sampled);
        Assert.True(sampled.SequenceEqual(sampled.OrderBy(x => x)));
    }

    [Fact]
    public async Task Progress_reports_phases()
    {
        var backend = new SegmentBackend(_ => new SegmentIdentification("r", "One", "A", 0.9,
            new[] { new ReleaseRef("REL-1", null, 0, 0) }));
        var release = new ReleaseInfo("REL-1", "Album", "A", 2000, new[]
        {
            (IReadOnlyList<ExpectedTrack>)new[]
            {
                new ExpectedTrack("One", "A", 101),
                new ExpectedTrack("Two", "A", 102),
                new ExpectedTrack("Three", "A", 103),
            },
        });
        var analyzer = Analyzer(backend, (_, _) => Task.FromResult<ReleaseInfo?>(release));
        var phases = new List<string>();
        var progress = new SyncProgress<SplitAnalysisProgress>(p => phases.Add(p.Phase));

        var offline = await analyzer.AnalyzeOfflineAsync(Candidate(), progress);
        await analyzer.IdentifyAsync(offline, progress);

        Assert.Contains("detecting", phases);
        Assert.Contains("fingerprinting", phases);
        Assert.Contains("lookingup", phases);
        Assert.Contains("aligning", phases);
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}

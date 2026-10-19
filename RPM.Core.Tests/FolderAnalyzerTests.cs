using RPM.Core.Analysis;
using RPM.Core.Identify;
using RPM.Core.Models;

namespace RPM.Core.Tests;

public class FolderAnalyzerTests : IDisposable
{
    private readonly string _root;

    public FolderAnalyzerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mdtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void Touch(string name)
        => File.WriteAllText(Path.Combine(_root, name), "not-really-audio");

    [Fact]
    public async Task Analyzes_untagged_files_via_filename_guess()
    {
        Touch("Muse - Hysteria.mp3");
        Touch("junkname.mp3");

        var analyzer = new FolderAnalyzer(identificationBackend: null);
        var progressReports = new List<AnalysisProgress>();
        var tracks = await analyzer.AnalyzeAsync(
            _root,
            new SynchronousProgress<AnalysisProgress>(progressReports.Add));

        Assert.Equal(2, tracks.Count);

        var guessed = tracks.Single(t => t.FilePath.EndsWith("Muse - Hysteria.mp3"));
        Assert.Equal(MetadataSource.FilenameGuess, guessed.Source);
        Assert.Equal("Muse", guessed.Artist);

        var unknown = tracks.Single(t => t.FilePath.EndsWith("junkname.mp3"));
        Assert.Equal(MetadataSource.Unknown, unknown.Source);

        Assert.Equal(2, progressReports.Last().Processed);
        Assert.Equal(2, progressReports.Last().Total);
    }

    [Fact]
    public async Task Parallel_tag_read_preserves_file_order_and_count()
    {
        // Enough files that the parallel tag-read phase actually overlaps across cores.
        const int n = 60;
        for (int i = 0; i < n; i++)
            Touch($"{i:D3} - Artist - Title.mp3");

        var analyzer = new FolderAnalyzer(identificationBackend: null);
        var reports = new List<AnalysisProgress>();
        var tracks = await analyzer.AnalyzeAsync(
            _root, new SynchronousProgress<AnalysisProgress>(reports.Add));

        Assert.Equal(n, tracks.Count);

        // Output is placed by index, so it matches the scanner's sorted order whichever file's tag read
        // finished first: a parallel scan must not change the queue's order.
        var expected = Enumerable.Range(0, n)
            .Select(i => Path.Combine(_root, $"{i:D3} - Artist - Title.mp3"))
            .ToArray();
        Assert.Equal(expected, tracks.Select(t => t.FilePath).ToArray());

        // The tally reaches the total and the final report is a clean 100%.
        Assert.Equal(n, reports.Max(r => r.Processed));
        Assert.Equal(n, reports.Last().Processed);
        Assert.Equal(n, reports.Last().Total);
    }

    private static TrackIdentifier Identifier(IIdentificationBackend backend)
        => new(backend)
        {
            Fingerprinter = _ => new Fingerprint.Chroma.FpcalcRunner.CompressedResult(200, "ZmFrZQ=="),
            RawFingerprinter = _ => null,
        };

    [Fact]
    public async Task Backend_failures_fall_back_to_filename()
    {
        Touch("Muse - Hysteria.mp3");

        var analyzer = new FolderAnalyzer(Identifier(new ThrowingBackend(new InvalidOperationException("boom"))));
        var tracks = await analyzer.AnalyzeAsync(_root);

        var t = Assert.Single(tracks);
        Assert.Equal(MetadataSource.FilenameGuess, t.Source);
        Assert.False(analyzer.RecognitionRefused);
    }

    [Fact]
    public async Task Supporters_only_refusal_disables_identification_and_is_flagged()
    {
        Touch("a - b.mp3");
        Touch("c - d.mp3");

        var backend = new ThrowingBackend(new LambdaIdentificationBackend.SupportersOnlyException("supporters only"));
        var analyzer = new FolderAnalyzer(Identifier(backend));
        var tracks = await analyzer.AnalyzeAsync(_root);

        Assert.True(analyzer.RecognitionRefused);
        Assert.Equal(1, backend.Calls); // second file skips identification entirely
        Assert.All(tracks, t => Assert.Equal(MetadataSource.FilenameGuess, t.Source));
    }

    [Fact]
    public async Task Identified_files_carry_fingerprint_metadata()
    {
        Touch("junkname.mp3");

        var backend = new StaticBackend(new IdentificationResult("Hysteria", "Muse", "Absolution", 2003, 0.93, "own-db"));
        var analyzer = new FolderAnalyzer(Identifier(backend));
        var tracks = await analyzer.AnalyzeAsync(_root);

        var t = Assert.Single(tracks);
        Assert.Equal(MetadataSource.Fingerprint, t.Source);
        Assert.Equal("Muse", t.Artist);
        Assert.Equal("Absolution", t.Album);
        Assert.Equal(2003u, t.Year);
        Assert.Equal(0.93, t.IdentificationScore);
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        Touch("a - b.mp3");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var analyzer = new FolderAnalyzer((TrackIdentifier?)null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => analyzer.AnalyzeAsync(_root, ct: cts.Token));
    }

    /// <summary>IProgress that invokes inline (no SynchronizationContext post) so asserts see all reports.
    /// Locked because FolderAnalyzer's tag-read phase reports from several threads at once (the real app's
    /// <see cref="Progress{T}"/> serialises on the UI thread; this stands in for that).</summary>
    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        private readonly object _gate = new();
        public void Report(T value) { lock (_gate) handler(value); }
    }

    private sealed class StaticBackend(IdentificationResult? result) : IIdentificationBackend
    {
        public Task<IdentificationResult?> IdentifyAsync(FingerprintQuery query, CancellationToken ct = default)
            => Task.FromResult(result);
    }

    private sealed class ThrowingBackend(Exception ex) : IIdentificationBackend
    {
        public int Calls { get; private set; }

        public Task<IdentificationResult?> IdentifyAsync(FingerprintQuery query, CancellationToken ct = default)
        {
            Calls++;
            throw ex;
        }
    }
}

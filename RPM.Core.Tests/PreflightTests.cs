using RPM.Core.Startup;

namespace RPM.Core.Tests;

public class PreflightTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"preflight-{Guid.NewGuid():N}");

    public PreflightTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string Write(string name, int bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private static PreflightItem Item(
        string name, Func<string?> locate,
        PreflightSeverity severity = PreflightSeverity.Required, long min = 1024)
        => new(name, $"Preflight_{name}", locate, severity, min);

    [Fact]
    public void A_complete_install_starts()
    {
        var path = Write("ffmpeg", 200_000);
        var report = Preflight.Check([Item("ffmpeg", () => path)]);

        Assert.Empty(report.Failures);
        Assert.True(report.CanStart);
    }

    [Fact]
    public void A_missing_required_file_stops_the_app()
    {
        var report = Preflight.Check(
            [Item("beat_this.onnx", () => Path.Combine(_root, "nope.onnx"))]);

        var failure = Assert.Single(report.Failures);
        Assert.Equal(PreflightFault.Missing, failure.Fault);
        Assert.False(report.CanStart);
        Assert.Single(report.Blocking);
    }

    [Fact]
    public void A_missing_optional_file_is_reported_but_still_starts()
    {
        var report = Preflight.Check(
            [Item("u2netp.onnx", () => Path.Combine(_root, "nope.onnx"), PreflightSeverity.Optional)]);

        Assert.Single(report.Failures);
        Assert.Single(report.Degraded);
        Assert.Empty(report.Blocking);
        Assert.True(report.CanStart);   // a lost feature is not a reason to refuse to open
    }

    /// <summary>The file left behind by an interrupted install or an antivirus quarantine: present,
    /// but not the real thing. "It exists" is not a good enough check for an 80 MB model.</summary>
    [Fact]
    public void A_truncated_file_is_damaged_rather_than_present()
    {
        var path = Write("beat_this.onnx", 12);
        var report = Preflight.Check([Item("beat_this.onnx", () => path, min: 40_000_000)]);

        var failure = Assert.Single(report.Failures);
        Assert.Equal(PreflightFault.Damaged, failure.Fault);
        Assert.Equal(path, failure.Path);
        Assert.Contains("12", failure.Detail);
        Assert.False(report.CanStart);
    }

    /// <summary>A tool resolver returns a bare name when it has found nothing anywhere. That is not a
    /// path. The manifest turns it into null, and the app must not read it as "found".</summary>
    [Fact]
    public void An_unresolved_tool_counts_as_missing()
    {
        var report = Preflight.Check([Item("fpcalc", () => null)]);

        Assert.Equal(PreflightFault.Missing, Assert.Single(report.Failures).Fault);
        Assert.False(report.CanStart);
    }

    /// <summary>The check is the safety net, so it cannot be the thing that stops the launch.</summary>
    [Fact]
    public void A_resolver_that_throws_is_reported_not_raised()
    {
        var report = Preflight.Check(
            [Item("ffmpeg", () => throw new InvalidOperationException("resolver exploded"))]);

        var failure = Assert.Single(report.Failures);
        Assert.Equal(PreflightFault.Missing, failure.Fault);
        Assert.Contains("resolver exploded", failure.Detail);
    }

    [Fact]
    public void Failures_keep_manifest_order_across_both_severities()
    {
        var report = Preflight.Check([
            Item("ffmpeg", () => null),
            Item("u2netp.onnx", () => null, PreflightSeverity.Optional),
            Item("fpcalc", () => null),
        ]);

        Assert.Equal(["ffmpeg", "u2netp.onnx", "fpcalc"], report.Failures.Select(f => f.Item.Name));
        Assert.Equal(["ffmpeg", "fpcalc"], report.Blocking.Select(f => f.Item.Name));
        Assert.Equal(["u2netp.onnx"], report.Degraded.Select(f => f.Item.Name));
    }

    [Fact]
    public void The_description_names_every_failure_and_its_severity()
    {
        var report = Preflight.Check([
            Item("ffmpeg", () => null),
            Item("u2netp.onnx", () => null, PreflightSeverity.Optional),
        ]);

        var text = Preflight.Describe(report, _root);

        Assert.Contains(_root, text);
        Assert.Contains("[REQUIRED] ffmpeg: missing", text);
        Assert.Contains("[optional] u2netp.onnx: missing", text);
    }

    [Fact]
    public void A_clean_report_says_so()
        => Assert.Contains("everything present", Preflight.Describe(Preflight.Check([]), _root));
}

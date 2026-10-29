using RPM.Core.Cloud;
using RPM.Core.Crashes;

namespace RPM.Core.Tests;

/// <summary>What an anonymous report leaves out, and what a tester's report keeps.</summary>
public class ReportScopeTests
{
    private static FeedbackReport Feedback(ReportScope scope) => scope.Feedback(
        "problem", "playback", "The record keeps spinning.", "mac", "0.1.17", "macOS 26.5", "arm64",
        "install-1", "said boot: app ctor", "iVBORw0KGgo=", 638_000_000_000_000_000, "tester@example.com");

    private static readonly PendingCrash Pending = new(
        "crash_0123456789abcdef01234567", "mac", "0.1.17", "macOS 26.5", "arm64", 1,
        "System.Exception: boom", null, null, "said boot: app ctor");

    [Fact]
    public void Anonymous_feedback_has_no_log_picture_or_reply_address()
    {
        var report = Feedback(ReportScope.Anonymous);

        Assert.Null(report.Recent);
        Assert.Null(report.ScreenshotPng);
        Assert.Null(report.ReplyTo);
        Assert.Null(report.Scope);
        Assert.Equal("The record keeps spinning.", report.Message);
        Assert.Equal("install-1", report.InstallId);
    }

    [Fact]
    public void Full_feedback_keeps_all_of_it_and_says_so()
    {
        var report = Feedback(ReportScope.Full);

        Assert.Equal("said boot: app ctor", report.Recent);
        Assert.Equal("iVBORw0KGgo=", report.ScreenshotPng);
        Assert.Equal("tester@example.com", report.ReplyTo);
        Assert.Equal("full", report.Scope);
    }

    [Fact]
    public void Anonymous_crash_keeps_the_stack_but_not_the_log()
    {
        var report = Pending.ToReport("install-1", ReportScope.Anonymous);

        Assert.Null(report.Recent);
        Assert.Null(report.Scope);
        Assert.Equal("System.Exception: boom", report.Exception);
        Assert.Equal("install-1", report.InstallId);
    }

    [Fact]
    public void Full_crash_keeps_the_log()
    {
        var report = Pending.ToReport("install-1", ReportScope.Full);

        Assert.Equal("said boot: app ctor", report.Recent);
        Assert.Equal("full", report.Scope);
    }

    /// <summary>The desktop has one binary for testers and everyone else, so the account decides.</summary>
    [Fact]
    public void Only_a_tester_account_is_full()
    {
        Assert.Same(ReportScope.Full, ReportScope.ForTester(true));
        Assert.Same(ReportScope.Anonymous, ReportScope.ForTester(false));
        Assert.Equal("full", ReportScope.Full.Wire);
        Assert.Null(ReportScope.Anonymous.Wire);
    }
}

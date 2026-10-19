using RPM.Core.Identify;
using RPM.Desktop.Presentation.Main.Models;

namespace RPM.Desktop.Tests;

/// <summary>
/// The Recognise report's AudD line is shown when <see cref="RecognitionReport.SentToAudD"/> is more than
/// nought, and states that count and <see cref="RecognitionReport.IdentifiedByAudD"/>. The line must only
/// count tracks that AudD was really asked about. For example, a track that AcoustID already named must not
/// be counted as sent to AudD. What is checked: the counts are counts of what AudD did, track by track.
/// </summary>
public class RecognitionReportAudDTests
{
    private static RecognitionOutcome Track(string file, bool matched, string? source, AudDOutcome audd) =>
        new(file, matched, matched ? "Title" : null, matched ? "Artist" : null, source, matched ? 0.97 : 0, null, audd);

    [Fact]
    public void A_run_the_free_layers_answered_sent_nothing_to_AudD()
    {
        var report = new RecognitionReport(
        [
            Track("track02.mp3", true, "acoustid", AudDOutcome.NotAsked),
            Track("track03.mp3", true, "acoustid", AudDOutcome.NotAsked),
            Track("track04.mp3", true, "own-db", AudDOutcome.NotAsked),
        ], Attempted: 3, Fatal: null);

        Assert.Equal(0, report.SentToAudD);
        Assert.Equal(0, report.IdentifiedByAudD);
        Assert.Equal(3, report.Recognized);
    }

    [Fact]
    public void The_counts_are_what_AudD_did()
    {
        var report = new RecognitionReport(
        [
            Track("track02.mp3", true, "acoustid", AudDOutcome.NotAsked),
            Track("track03.mp3", true, "audd", AudDOutcome.Identified),
            Track("track04.mp3", false, null, AudDOutcome.NotIdentified),
            // The server's second look in the rpm database named it before AudD was asked.
            Track("track05.mp3", true, "own-db", AudDOutcome.NotAsked),
            // Missed by the free layers, and AudD was never asked (the run was refused the layer).
            Track("track06.mp3", false, null, AudDOutcome.NotAsked),
        ], Attempted: 5, Fatal: null);

        Assert.Equal(2, report.SentToAudD);
        Assert.Equal(1, report.IdentifiedByAudD);
        Assert.Equal(3, report.Recognized);
    }

    [Fact]
    public void A_track_that_failed_is_not_counted_as_sent()
    {
        var failed = new RecognitionOutcome("track07.mp3", false, null, null, null, 0,
            RPM.Core.Diagnostics.Fault.Server, AudDOutcome.NotAsked);

        var report = new RecognitionReport([failed], Attempted: 1, Fatal: null);

        Assert.Equal(0, report.SentToAudD);
        Assert.Equal(1, report.Failed);
    }
}

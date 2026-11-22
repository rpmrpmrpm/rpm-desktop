using Microsoft.Extensions.Logging.Abstractions;
using RPM.Desktop.Services.Migration;

namespace RPM.Desktop.Tests;

/// <summary>
/// <see cref="MigrationService.MarkReportSeen"/>: the deck's import chip goes away once the report of a
/// finished import has been shown.
///
/// <para>The fault it prevents: the chip redraws only on <see cref="MigrationService.Changed"/>. The window
/// noted the report as seen without raising it, so the chip kept its full ring after the report was read,
/// and the finished import looked as if it was still running.</para>
/// </summary>
public class MigrationReportSeenTests
{
    // The constructor only keeps its arguments. Nothing here reaches the library, the cloud or the
    // sign-in.
    private static MigrationService Service() =>
        new(null!, null!, null!, null!, null!, NullLogger<MigrationService>.Instance);

    [Fact]
    public void Marking_the_report_seen_tells_the_chip()
    {
        var service = Service();
        var raised = 0;
        service.Changed += (_, _) => raised++;

        service.MarkReportSeen();

        Assert.True(service.ReportSeen);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Marking_it_again_raises_nothing_so_the_window_redraw_does_not_loop()
    {
        var service = Service();
        service.MarkReportSeen();
        var raised = 0;
        service.Changed += (_, _) => raised++;

        service.MarkReportSeen();

        Assert.Equal(0, raised);
    }
}

using System.Text.RegularExpressions;
using RPM.Desktop.Presentation.Controls;
using RPM.Desktop.Services.Platform;
using Step = RPM.Desktop.Services.Platform.PathData.Step;

namespace RPM.Desktop.Tests;

/// <summary>
/// <see cref="PathData"/>: SVG path data read into absolute points and fitted into a rectangle as a Path
/// with <c>Stretch="Uniform"</c> places it. The macOS splash cuts its window to the wordmark with it, so a
/// wrong point is a cut that does not lie over the drawn letters.
/// </summary>
public class PathDataTests
{
    [Fact]
    public void Wordmark_reads_as_four_closed_outlines()
    {
        var steps = PathData.Read(SweptMark.Wordmark);

        Assert.NotNull(steps);
        Assert.Equal(4, steps!.Count(s => s.Step == Step.Move));
        Assert.Equal(4, steps.Count(s => s.Step == Step.Close));
        Assert.Equal(Step.Move, steps[0].Step);
        Assert.Equal(Step.Close, steps[^1].Step);
    }

    [Fact]
    public void Wordmark_constant_is_the_geometry_the_xaml_draws()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "RPM.Desktop", "Presentation", "Controls", "SweptMark.xaml"));
        var drawn = Regex.Matches(xaml, "Data=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();

        Assert.Equal(2, drawn.Count);
        Assert.All(drawn, data => Assert.Equal(SweptMark.Wordmark, data));
    }

    [Fact]
    public void H_and_V_keep_the_other_coordinate()
    {
        var steps = PathData.Read("M2 3H10V7H2Z")!;

        Assert.Equal([(2.0, 3.0)], steps[0].Points);
        Assert.Equal([(10.0, 3.0)], steps[1].Points);
        Assert.Equal([(10.0, 7.0)], steps[2].Points);
        Assert.Equal([(2.0, 7.0)], steps[3].Points);
        Assert.Equal(Step.Close, steps[4].Step);
    }

    [Fact]
    public void Pairs_after_a_move_are_lines()
    {
        var steps = PathData.Read("M0 0 10 0 10 10Z")!;

        Assert.Equal([Step.Move, Step.Line, Step.Line, Step.Close], steps.Select(s => s.Step));
    }

    [Fact]
    public void A_close_returns_to_the_start_of_its_outline()
    {
        // After Z the current point is the outline's start, so the H after it is measured from there.
        var steps = PathData.Read("M1 2L5 6ZH9")!;

        Assert.Equal([(9.0, 2.0)], steps[^1].Points);
    }

    [Fact]
    public void A_curve_has_two_control_points_then_its_end()
    {
        var steps = PathData.Read("M0 0C1 2 3 4 5 6")!;

        Assert.Equal(Step.Curve, steps[1].Step);
        Assert.Equal([(1.0, 2.0), (3.0, 4.0), (5.0, 6.0)], steps[1].Points);
    }

    [Fact]
    public void Numbers_split_on_a_sign_and_read_points_and_exponents()
    {
        var steps = PathData.Read("M0-5L1.5-2.5,1e1 -2E-1")!;

        Assert.Equal([(0.0, -5.0)], steps[0].Points);
        Assert.Equal([(1.5, -2.5)], steps[1].Points);
        Assert.Equal([(10.0, -0.2)], steps[2].Points);
    }

    [Theory]
    [InlineData("m0 0l1 1z")]        // relative commands
    [InlineData("M0 0A1 1 0 0 1 2 2")] // an arc
    [InlineData("M0 0Q1 1 2 2")]     // a quadratic curve
    [InlineData("M0 0 1")]           // half a pair
    [InlineData("M0 0C1 1 2 2")]     // a curve short of its end
    [InlineData("M0 0Z1")]           // a number after Z
    [InlineData("M0 0L1 #")]         // something that is not a number
    [InlineData("1 2")]              // numbers before any command
    public void Data_this_cannot_read_is_refused(string data) =>
        Assert.Null(PathData.Read(data));

    [Fact]
    public void Fit_stretches_uniformly_and_centres_in_the_rectangle()
    {
        // 10 wide and 5 high into 40 by 40: the width sets the scale (4), so the shape is 40 by 20,
        // centred 10 down.
        var steps = PathData.Read("M0 0H10V5H0Z")!;

        var fitted = PathData.Fit(steps, new Windows.Foundation.Rect(100, 200, 40, 40))!;

        Assert.Equal([(100.0, 210.0)], fitted[0].Points);
        Assert.Equal([(140.0, 210.0)], fitted[1].Points);
        Assert.Equal([(140.0, 230.0)], fitted[2].Points);
        Assert.Equal([(100.0, 230.0)], fitted[3].Points);
    }

    [Fact]
    public void Fit_puts_the_wordmark_where_the_splash_card_draws_it()
    {
        // SplashCard.xaml: a 252 by 97 mark centred in the 300 by 180 card, so 24 in from either side.
        // The wordmark is 507 by 195, so the width sets the scale and the letters span it exactly.
        var fitted = PathData.Fit(PathData.Read(SweptMark.Wordmark)!, new Windows.Foundation.Rect(24, 41.5, 252, 97))!;
        var points = fitted.SelectMany(s => s.Points).ToList();

        var height = 195 * 252 / 507.0;
        Assert.Equal(24, points.Min(p => p.X), 9);
        Assert.Equal(276, points.Max(p => p.X), 9);
        Assert.Equal(41.5 + (97 - height) / 2, points.Min(p => p.Y), 9);
        Assert.Equal(41.5 + (97 + height) / 2, points.Max(p => p.Y), 9);
    }

    [Theory]
    [InlineData("M0 0H10", 40, 40)] // a line has no height
    [InlineData("M0 0H10V5Z", 0, 40)] // a rectangle with no width
    public void Fit_refuses_what_has_no_area(string data, double width, double height) =>
        Assert.Null(PathData.Fit(PathData.Read(data)!, new Windows.Foundation.Rect(0, 0, width, height)));

    /// <summary>Walks up from the test binaries to the folder holding RPM.sln.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RPM.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}

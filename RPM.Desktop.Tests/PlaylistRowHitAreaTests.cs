namespace RPM.Desktop.Tests;

/// <summary>
/// A press anywhere on a track row plays it. The rule is that the two row gestures are attached to the
/// LIST, and these guards keep them there.
///
/// <para><b>Why they cannot go back on the template.</b> The row is 64 tall. Its container template stacks
/// Fill and Hover. Both have a transparent background, so both can be hit across the whole row. Then comes
/// the ContentPresenter, with Margin="22,0". The presenter's child is the row template, centred at a
/// desired height of 56. So the template covers x 22 to W-22 and y 4 to 60. A press outside that area lands
/// on Hover, which is a SIBLING of the presenter, not a parent of the template. A Tapped raised there
/// bubbles to the ListViewItem and the ListView, and never passes through the template. So a handler
/// written on the template is not called. The row selects, and nothing plays. The uncovered area is 22
/// points at each end of the row and a 4-point band along the top and bottom edges.</para>
///
/// <para>This was measured, not reasoned out. Of 38 presses on the Skia head, 35 raised Tapped. The three
/// that went missing all reported a Border as the source. All 35 that arrived reported the template's Grid
/// or a TextBlock inside it.</para>
///
/// <para>These tests read the markup and the code-behind as text, because the rule is made of text. The
/// handler is attached in one place and must not be attached in the other. See
/// <see cref="PlaylistRowFaceBindingTests"/>, whose checks are written the same way for the same
/// reason.</para>
/// </summary>
public class PlaylistRowHitAreaTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "RPM.Desktop")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found from the test binary");
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([Root, "RPM.Desktop", .. parts]));

    private static string Markup() => Read("Presentation", "Main", "RightPane", "PlaylistView.xaml");

    private static string CodeBehind() => Read("Presentation", "Main", "RightPane", "PlaylistView.xaml.cs");

    /// <summary>The row template, and only it.</summary>
    private static string RowTemplate()
    {
        var page = Markup();
        var at = page.IndexOf("<DataTemplate x:DataType=\"model:PlaylistTrackRow\">", StringComparison.Ordinal);
        Assert.True(at > 0, "the playlist's row template was renamed or moved");
        var end = page.IndexOf("</DataTemplate>", at, StringComparison.Ordinal);
        return page[at..end];
    }

    [Fact]
    public void TheRowTemplateCarriesNeitherGesture()
    {
        var template = RowTemplate();

        Assert.DoesNotContain("Tapped=", template);
        Assert.DoesNotContain("ContextRequested=", template);
    }

    [Fact]
    public void TheListCarriesBothGestures()
    {
        var code = CodeBehind();

        Assert.Contains("DetailList.AddHandler(TappedEvent", code);
        Assert.Contains("DetailList.AddHandler(ContextRequestedEvent", code);
    }

    /// <summary>
    /// Both are added with handledEventsToo. A ListViewItem marks the gestures it acts on handled on the
    /// way past, so a handler that waited its turn would not be called on the rows that select.
    /// </summary>
    [Fact]
    public void BothGesturesAreAddedHandledEventsToo()
    {
        var code = CodeBehind();
        var at = code.IndexOf("DetailList.AddHandler(TappedEvent", StringComparison.Ordinal);
        Assert.True(at > 0);

        // The two calls and nothing else: each ends at the ");" that closes it.
        var block = code[at..code.IndexOf("ContextRequestedEventArgs>(Track_ContextRequested), true);",
                                          at, StringComparison.Ordinal)];
        Assert.Contains("Track_Tapped), true);", block);
    }

    /// <summary>
    /// Which row was pressed is read off what the pointer hit, not off the handler's sender. With the
    /// handlers on the list the sender IS the list, so a sender-keyed read would find no row at all.
    /// </summary>
    [Fact]
    public void BothHandlersReadTheRowOffWhatWasHit()
    {
        var code = CodeBehind();

        foreach (var handler in new[] { "private void Track_Tapped(", "private void Track_ContextRequested(" })
        {
            var at = code.IndexOf(handler, StringComparison.Ordinal);
            Assert.True(at > 0, $"{handler} was renamed — this guard is looking for the row gestures.");

            var body = code[at..code.IndexOf("\n    }", at, StringComparison.Ordinal)];
            Assert.Contains("TrackRowUnder(e.OriginalSource)", body);
            Assert.DoesNotContain("sender as FrameworkElement", body);
        }
    }
}

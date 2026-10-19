using System.Diagnostics;
using RPM.Desktop.Services.Shell;

namespace RPM.Desktop.Tests;

/// <summary>
/// A throw is reported against the line that made it, written the way it was written in the source.
///
/// <para><b>Why this is worth testing.</b> The frame directly under a throw is nearly always the
/// framework's. For asynchronous code, the frame under that is a compiler-made type whose only method is
/// <c>MoveNext</c>. If the code took either frame, every asynchronous site would be reported as "MoveNext".
/// All of them would be counted in one large row that means nothing. A wrong name looks as believable as a
/// right one, so this is easy to miss.</para>
/// </summary>
public class ExceptionSiteTests
{
    [Fact]
    public void The_site_is_the_first_frame_of_ours_and_not_the_framework_that_threw()
    {
        string? site = null;

        try
        {
            // Thrown by the framework, so the frames under the throw belong to it. What is wanted is
            // this method.
            var one = new int[1];
            var past = one.Length + 1;
            _ = one[past];
        }
        catch (IndexOutOfRangeException)
        {
            site = ExceptionTrace.SiteOf(new StackTrace(fNeedFileInfo: false));
        }

        Assert.Equal(
            $"{nameof(ExceptionSiteTests)}.{nameof(The_site_is_the_first_frame_of_ours_and_not_the_framework_that_threw)}",
            site);
    }

    [Fact]
    public async Task An_async_method_is_named_by_what_it_was_called_and_never_MoveNext()
    {
        var site = await Deep();

        Assert.Equal($"{nameof(ExceptionSiteTests)}.{nameof(Deep)}", site);
    }

    [Fact]
    public void A_frame_inside_a_lambda_is_named_by_the_method_the_lambda_was_written_in()
    {
        var site = new Func<string?>(() => ExceptionTrace.SiteOf(new StackTrace(fNeedFileInfo: false)))();

        Assert.Equal(
            $"{nameof(ExceptionSiteTests)}.{nameof(A_frame_inside_a_lambda_is_named_by_the_method_the_lambda_was_written_in)}",
            site);
    }

    [Fact]
    public void A_throw_from_the_toolkit_names_the_toolkit_and_the_line_of_ours_underneath_it()
    {
        // A throw inside the renderer or the toolkit has no frame of ours near it. Taking the nearest RPM
        // frame would name "Program.Main", which is true of everything on the drawing thread and tells
        // nothing.
        var site = Elsewhere.Toolkit.Dispatcher.Ask();

        Assert.Equal(
            "Dispatcher.Ask <- " +
            $"{nameof(ExceptionSiteTests)}.{nameof(A_throw_from_the_toolkit_names_the_toolkit_and_the_line_of_ours_underneath_it)}",
            site);
    }

    /// <summary>This method is asynchronous and is awaited before the stack is read. So the frame is the
    /// state machine's, not this method's own frame.</summary>
    private static async Task<string?> Deep()
    {
        await Task.Yield();
        return ExceptionTrace.SiteOf(new StackTrace(fNeedFileInfo: false));
    }
}

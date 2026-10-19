using RPM.Core.Cloud;
using RPM.Fingerprint;

namespace RPM.Core.Tests;

/// <summary>
/// What the cloud's log book keeps about a failure beside the sentence the user is shown, and what a
/// report from the Errors card sends. The full text is kept so that the reason a track was refused
/// can be read back later, not only the short sentence.
/// </summary>
public class CloudFullErrorTests
{
    /// <summary>The exception as the upload's catch sees it: thrown, so it has a stack.</summary>
    private static Exception Thrown(Exception ex)
    {
        try { throw ex; }
        catch (Exception caught) { return caught; }
    }

    [Fact]
    public void Everything_ffmpeg_said_is_kept_and_the_message_stays_short()
    {
        var ex = Thrown(new FfmpegFailedException(
            "ffmpeg could not canonicalize 'a.flac': [flac] invalid sync code",
            "[flac @ 01] invalid sync code\n[flac @ 01] decode_frame() failed\nConversion failed!\n"));

        var full = CloudFailure.InFull(ex, home: "");

        Assert.StartsWith("FfmpegFailedException: ffmpeg could not canonicalize 'a.flac'", full);
        Assert.Contains("ffmpeg:", full);
        Assert.Contains("decode_frame() failed", full);
        Assert.Contains("Conversion failed!", full);
        Assert.Contains(" at ", full);
    }

    /// <summary>The console prints <see cref="Exception.ToString"/> for every logged failure, and the
    /// flood <see cref="FfmpegSaid"/> keeps out of it must stay out.</summary>
    [Fact]
    public void A_logged_ffmpeg_failure_still_prints_only_the_short_message()
    {
        var ex = new FfmpegFailedException("ffmpeg could not canonicalize 'a.flac': short", "the whole flood");

        Assert.DoesNotContain("the whole flood", ex.ToString());
    }

    [Fact]
    public void Every_exception_down_the_chain_is_named()
    {
        var ex = Thrown(new HttpRequestException("The request failed.", new IOException("Connection reset.")));

        var full = CloudFailure.InFull(ex, home: "");

        Assert.Contains("HttpRequestException: The request failed.", full);
        Assert.Contains("IOException: Connection reset.", full);
    }

    /// <summary>ffmpeg repeats the path it was given, in whichever slash direction it was given.
    /// Crash reports remove the user's name from home-folder paths, and so does this.</summary>
    [Fact]
    public void The_home_folder_is_written_as_a_tilde()
    {
        // Not thrown: a stack carries the source paths of the machine that BUILT the tests, which are
        // not the user's home and on a Mac never match a Windows one. This is about the messages.
        var ex = new FfmpegFailedException(
            @"ffmpeg could not canonicalize 'C:\Users\alex\Music\a.flac': refused",
            "Error opening input file C:/Users/Alex/Music/a.flac.");

        var full = CloudFailure.InFull(ex, home: @"C:\Users\alex");

        Assert.Contains(@"~\Music\a.flac", full);
        Assert.Contains("~/Music/a.flac", full);
        Assert.DoesNotContain("alex", full, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void It_fits_what_the_server_keeps()
    {
        var flood = string.Concat(Enumerable.Repeat("[aac @ 01] channel element 3.12 is not allocated\n", 1_000));
        var ex = Thrown(new FfmpegFailedException("ffmpeg could not transcode to 128k: [aac] flood", flood));

        var full = CloudFailure.InFull(ex, home: "");

        Assert.Equal(CloudFailure.MaxFullError, full.Length);
        Assert.StartsWith("FfmpegFailedException:", full);
    }
}

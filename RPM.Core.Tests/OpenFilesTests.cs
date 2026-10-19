using RPM.Core.Cloud;

namespace RPM.Core.Tests;

/// <summary>
/// The check that prevents rewriting the file of the song that is playing.
///
/// <para>
/// Each case here is a mistake that would break the music during playback: a comparison that fails
/// because of letter case, a next track the gapless changeover has already opened but nobody
/// checked, a null read as a match.
/// </para>
/// </summary>
public class OpenFilesTests
{
    private const string Playing = "Band/Record/01 One.flac";
    private const string Next = "Band/Record/02 Two.flac";
    private const string Other = "Band/Record/03 Three.flac";

    [Fact]
    public void The_playing_file_is_held()
        => Assert.True(OpenFiles.HeldOpen(Playing, Playing, Next));

    [Fact]
    public void The_successor_already_opened_is_held_too()
    {
        // The gapless join opens the next track about twelve seconds before it is needed. That file
        // is as open as the one playing, and deleting it would break the changeover it was opened
        // for.
        Assert.True(OpenFiles.HeldOpen(Next, Playing, Next));
    }

    [Fact]
    public void Anything_else_is_free_to_go()
        => Assert.False(OpenFiles.HeldOpen(Other, Playing, Next));

    [Fact]
    public void Case_alone_never_makes_it_a_different_file()
    {
        // On the platforms rpm runs on these are one file. Treating them as two would answer "safe to
        // delete" about the track that is playing.
        Assert.True(OpenFiles.HeldOpen("BAND/RECORD/01 ONE.FLAC", Playing, null));
        Assert.True(OpenFiles.HeldOpen(Playing, "BAND/RECORD/01 ONE.FLAC", null));
    }

    [Fact]
    public void A_silent_player_holds_nothing()
    {
        Assert.False(OpenFiles.HeldOpen(Playing, null, null));
        Assert.False(OpenFiles.HeldOpen(Playing, "", ""));
    }

    [Fact]
    public void A_streamed_successor_holds_no_file()
    {
        // A cloud track's next track is a signed URL, so the engine has no path to report for it. That
        // must mean "no file held", not "something unknown is held".
        Assert.False(OpenFiles.HeldOpen(Other, Playing, queuedNext: null));
    }

    [Fact]
    public void Nothing_is_asked_about_a_track_with_no_path()
    {
        Assert.False(OpenFiles.HeldOpen(null, Playing, Next));
        Assert.False(OpenFiles.HeldOpen("", Playing, Next));
    }
}

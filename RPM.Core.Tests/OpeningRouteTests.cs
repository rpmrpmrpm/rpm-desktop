using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// The opening decision. Two cases are guarded: a configured folder whose mirror holds nothing must
/// not look like a collection, and the three failures must not look like a first run.
/// </summary>
public class OpeningRouteTests
{
    [Fact]
    public void NoFolderGoesToTheFolderStep()
        => Assert.Equal(OpeningRoute.PickFolder, OpeningScreen.Choose(new LibraryOpening.NoFolder()));

    [Fact]
    public void AnEmptyLibraryGoesToTheImportStep()
        => Assert.Equal(OpeningRoute.ImportFirstMusic,
            OpeningScreen.Choose(new LibraryOpening.Opened("/Music", Albums: 0)));

    /// <summary>One album is a collection. The import step is for a library with nothing in it, not
    /// for a small one: a listener who has imported a single record and come back must not be sent
    /// through setup again.</summary>
    [Fact]
    public void OneAlbumIsEnoughToOpenTheLibrary()
        => Assert.Equal(OpeningRoute.Library,
            OpeningScreen.Choose(new LibraryOpening.Opened("/Music", Albums: 1)));

    [Fact]
    public void AStockedLibraryOpens()
        => Assert.Equal(OpeningRoute.Library,
            OpeningScreen.Choose(new LibraryOpening.Opened("/Music", Albums: 812)));

    /// <summary>
    /// An unmounted disk, a database from a newer build, and a database that will not open. None of
    /// them may answer PickFolder, which is a first run: the wizard would open at "Where should your
    /// music live?" with no message, and the folder picked there would replace the saved one.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryFailure))]
    public void AFailureGoesToTheProblemScreen(LibraryOpening.Trouble trouble)
        => Assert.Equal(OpeningRoute.CannotOpen, OpeningScreen.Choose(trouble));

    /// <summary>A failure keeps the folder it is about, so the screen can name it.</summary>
    [Theory]
    [MemberData(nameof(EveryFailure))]
    public void AFailureKeepsItsFolder(LibraryOpening.Trouble trouble)
        => Assert.Equal("/Volumes/gone", trouble.Folder);

    /// <summary>The newer-build case keeps both schema numbers, which its screen shows.</summary>
    [Fact]
    public void TheNewerCaseKeepsBothVersions()
    {
        var trouble = new LibraryOpening.Trouble.TooNew("/Music", Found: 71, Supported: 67);
        Assert.Equal(71, trouble.Found);
        Assert.Equal(67, trouble.Supported);
    }

    /// <summary>Two failures of the same kind for the same folder are equal. The problem screen uses
    /// this to tell "Try again hit the same problem" from a new failure.</summary>
    [Fact]
    public void TheSameFailureTwiceIsTheSameValue()
    {
        Assert.Equal(new LibraryOpening.Trouble.FolderMissing("/Music"),
                     new LibraryOpening.Trouble.FolderMissing("/Music"));
        Assert.NotEqual<LibraryOpening>(new LibraryOpening.Trouble.FolderMissing("/Music"),
                                        new LibraryOpening.Trouble.Unreadable("/Music", "disk I/O error"));
    }

    public static TheoryData<LibraryOpening.Trouble> EveryFailure() => new()
    {
        new LibraryOpening.Trouble.FolderMissing("/Volumes/gone"),
        new LibraryOpening.Trouble.TooNew("/Volumes/gone", Found: 71, Supported: 67),
        new LibraryOpening.Trouble.Unreadable("/Volumes/gone", "database disk image is malformed"),
    };
}

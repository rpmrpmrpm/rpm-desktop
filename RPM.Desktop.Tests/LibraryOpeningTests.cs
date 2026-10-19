using Microsoft.Data.Sqlite;
using RPM.Core.Library;
using RPM.Desktop.Services.Account;
using RPM.Desktop.Services.Library;
using RPM.Desktop.Services.Shell;

namespace RPM.Desktop.Tests;

/// <summary>
/// What the app makes of the saved music folder at launch.
///
/// <para>Each of these failures has its own answer. An unmounted disk, a database from a newer build, and a
/// database that will not open must not all look like a first run. A first run opens the setup wizard, and
/// a folder picked there would replace the saved one. Each failure has its own result, which the problem
/// screen reads.</para>
/// </summary>
public class LibraryOpeningTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"rpm-opening-{Guid.NewGuid():N}");

    private readonly LibraryService _library =
        new(TestSessions.SignedOut());

    public void Dispose()
    {
        _library.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_saved_folder_is_a_first_run(string? path)
    {
        Assert.IsType<LibraryOpening.NoFolder>(_library.OpenForLanding(path));
        Assert.Equal(OpeningRoute.PickFolder, OpeningScreen.Choose(_library.OpenForLanding(path)));
    }

    /// <summary>An unmounted disk, a folder renamed outside the app, a deleted folder.</summary>
    [Fact]
    public void A_folder_that_is_not_there_is_not_a_first_run()
    {
        var gone = Path.Combine(_root, "not-mounted");

        var opening = _library.OpenForLanding(gone);

        var missing = Assert.IsType<LibraryOpening.Trouble.FolderMissing>(opening);
        Assert.Equal(gone, missing.Folder);
        Assert.Equal(OpeningRoute.CannotOpen, OpeningScreen.Choose(opening));
    }

    [Fact]
    public void An_empty_folder_opens_with_no_albums_in_it()
    {
        Directory.CreateDirectory(_root);

        var opening = _library.OpenForLanding(_root);

        var opened = Assert.IsType<LibraryOpening.Opened>(opening);
        Assert.Equal(_root, opened.Folder);
        Assert.Equal(0, opened.Albums);
        Assert.Equal(OpeningRoute.ImportFirstMusic, OpeningScreen.Choose(opening));
    }

    /// <summary>
    /// A library that was opened by a newer build of rpm. The mirror is left as that build wrote it. Both
    /// schema versions are passed to the screen, which shows them.
    /// </summary>
    [Fact]
    public void A_database_from_a_newer_build_says_both_versions()
    {
        Directory.CreateDirectory(_root);
        using (new LibraryDatabase(MirrorLocation.InFolder(_root))) { }

        long current;
        using (var raw = new SqliteConnection($"Data Source={MirrorLocation.InFolder(_root).DatabasePath};Pooling=False"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "PRAGMA user_version";
            current = Convert.ToInt64(cmd.ExecuteScalar());
            cmd.CommandText = $"PRAGMA user_version = {current + 1}";
            cmd.ExecuteNonQuery();
        }

        var opening = _library.OpenForLanding(_root);

        var tooNew = Assert.IsType<LibraryOpening.Trouble.TooNew>(opening);
        Assert.Equal(_root, tooNew.Folder);
        Assert.Equal(current + 1, tooNew.Found);
        Assert.Equal(current, tooNew.Supported);
        Assert.Equal(OpeningRoute.CannotOpen, OpeningScreen.Choose(opening));
    }

    /// <summary>A mirror that is not a database at all. This simulates a corrupt file by writing bytes over
    /// the mirror.</summary>
    [Fact]
    public void A_database_that_will_not_open_carries_its_reason()
    {
        Directory.CreateDirectory(_root);
        var file = MirrorLocation.InFolder(_root).DatabasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "this is not a database");

        var opening = _library.OpenForLanding(_root);

        var unreadable = Assert.IsType<LibraryOpening.Trouble.Unreadable>(opening);
        Assert.Equal(_root, unreadable.Folder);
        Assert.NotEmpty(unreadable.Detail);
        Assert.Equal(OpeningRoute.CannotOpen, OpeningScreen.Choose(opening));
    }

    /// <summary>A failed open leaves the open library unchanged. The problem screen is shown over the
    /// current library. If a retry fails again, nothing has changed.</summary>
    [Fact]
    public void A_failed_open_leaves_the_open_library_where_it_was()
    {
        Directory.CreateDirectory(_root);
        Assert.IsType<LibraryOpening.Opened>(_library.OpenForLanding(_root));
        var opened = _library.Db;

        Assert.IsType<LibraryOpening.Trouble.FolderMissing>(
            _library.OpenForLanding(Path.Combine(_root, "not-mounted")));

        Assert.Same(opened, _library.Db);
    }
}

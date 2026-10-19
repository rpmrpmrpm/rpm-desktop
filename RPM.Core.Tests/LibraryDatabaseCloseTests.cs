using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// A mirror that has been closed, and what a caller that still holds it is told.
///
/// <para>Background passes hold a database for a whole pass, not for one call: the fingerprint sweep
/// takes it when it starts and uses it again after a submission that can take up to a minute. The
/// window can close, or another library can be opened, during that time. The mirror must refuse the
/// call before the caller reaches the connection. If the refusal is left to the driver, it is not
/// reliable: sometimes "ExecuteNonQuery can only be called when the connection is open", and
/// sometimes a handle closed while a statement is running.</para>
/// </summary>
public class LibraryDatabaseCloseTests : IDisposable
{
    private readonly string _root;

    public LibraryDatabaseCloseTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mdclose-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private LibraryDatabase Open() => new(MirrorLocation.InFolder(_root));

    private static LibraryTrack Track(string relativePath) => new()
    {
        RelativePath = relativePath,
        Folder = relativePath[..relativePath.LastIndexOf('/')],
        FileName = relativePath[(relativePath.LastIndexOf('/') + 1)..],
        Extension = Path.GetExtension(relativePath),
        SizeBytes = 5_000_000,
        ModifiedUtcTicks = 1,
    };

    [Fact]
    public void A_write_after_the_mirror_is_closed_is_refused_by_name()
    {
        var db = Open();
        db.Upsert(Track("Pink Floyd/The Wall/01 In the Flesh.flac"));
        db.Dispose();

        // The write the fingerprint pass makes at the end. ObjectDisposedException rather than the
        // driver's InvalidOperationException, and thrown before the connection is used.
        Assert.Throws<ObjectDisposedException>(() =>
            db.MarkFingerprintSubmitted("Pink Floyd/The Wall/01 In the Flesh.flac", 1, 0));
    }

    [Fact]
    public void A_read_after_the_mirror_is_closed_is_refused_the_same_way()
    {
        var db = Open();
        db.Dispose();

        Assert.Throws<ObjectDisposedException>(() => db.Query());
    }

    [Fact]
    public void Closing_twice_is_not_an_error()
    {
        // LibraryService disposes the old mirror when a different library is opened, and again when
        // the app exits.
        var db = Open();
        db.Dispose();
        db.Dispose();
    }

    [Fact]
    public void A_write_that_started_before_the_close_finishes()
    {
        // The other side: a call that is already running is not stopped, and the row it wrote is in
        // the file afterwards.
        var db = Open();
        db.Upsert(Track("Pink Floyd/The Wall/02 The Thin Ice.flac"));
        db.Dispose();

        using var reopened = Open();
        Assert.Single(reopened.Query("Thin Ice"));
    }
}

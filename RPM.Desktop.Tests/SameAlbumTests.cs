using RPM.Desktop.Services.Audio;
using RPM.Desktop.Services.Library;

namespace RPM.Desktop.Tests;

/// <summary>
/// Whether two queued tracks are one record decides which levelling reading a start uses. The queue asks
/// before every start, on the UI thread, so the answer comes from the two paths and not from rows.
/// </summary>
public class SameAlbumTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "rpm-same-album-tests", Guid.NewGuid().ToString("n"));

    private readonly LibraryService _library = new(TestSessions.SignedOut());
    private readonly NormalizationService _levels;

    public SameAlbumTests()
    {
        Directory.CreateDirectory(_dir);
        _library.Open(_dir);
        _levels = new NormalizationService(_library, new RPM.Desktop.Services.Shell.SettingsService());
    }

    public void Dispose()
    {
        _library.Dispose();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string At(params string[] parts) => Path.Combine([_dir, .. parts]);

    [Fact]
    public void Two_tracks_in_one_folder_are_one_record_whether_or_not_they_have_rows()
        => Assert.True(_levels.SameAlbum(At("Artist", "Record", "01.flac"), At("Artist", "Record", "02.flac")));

    [Fact]
    public void Tracks_in_two_folders_are_two_records()
        => Assert.False(_levels.SameAlbum(At("Artist", "Record", "01.flac"), At("Artist", "Other", "01.flac")));

    [Fact]
    public void A_folder_is_the_same_folder_in_any_case()
        => Assert.True(_levels.SameAlbum(At("Artist", "Record", "01.flac"), At("artist", "RECORD", "02.flac")));

    [Fact]
    public void A_file_outside_the_collection_is_on_no_record()
    {
        var outside = Path.Combine(Path.GetTempPath(), "rpm-same-album-outside", "Record", "01.flac");
        Assert.False(_levels.SameAlbum(At("Record", "01.flac"), outside));
        Assert.False(_levels.SameAlbum(null, At("Record", "01.flac")));
    }
}

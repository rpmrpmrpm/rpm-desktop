using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// How far the measuring pass has got, which is what decides whether a mood list is short of music
/// or short of time.
///
/// <para>The rule worth pinning is the denominator. A record that is only in the cloud has no file
/// on this disk, so nothing will ever measure it, and counting it as outstanding work is a pass that
/// can never finish: with 73 such rows out of 629 the fraction stays at 88%, under the nine tenths
/// <see cref="MeasuringProgress.Enough"/> asks for, and the mood lists would keep saying "still
/// measuring your music" over a pass that had finished.</para>
/// </summary>
public class MeasuredSoFarTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public MeasuredSoFarTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmlib-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private void Add(string name, bool measured, CloudState state = CloudState.Local)
    {
        _db.Upsert(new LibraryTrack
        {
            RelativePath = name,
            Folder = "",
            FileName = name,
            Extension = ".flac",
            SizeBytes = 100,
            ModifiedUtcTicks = 1,
            DurationSeconds = 200,
            CloudState = state,
        });

        if (measured)
            _db.SaveTrackMood(name, 100, 1, new Analysis.MoodReading(0.5, 0.5, 0.5));
    }

    [Fact]
    public void A_shelf_nobody_has_measured_is_nought_of_its_tracks()
    {
        Add("a.flac", measured: false);
        Add("b.flac", measured: false);

        var progress = _db.MeasuredSoFar(TrackScope.OnThisComputer);

        Assert.Equal(0, progress.Measured);
        Assert.Equal(2, progress.Total);
        Assert.False(progress.Enough);
    }

    [Fact]
    public void A_reading_moves_the_number_the_moment_it_is_written()
    {
        Add("a.flac", measured: true);
        Add("b.flac", measured: false);

        Assert.Equal(1, _db.MeasuredSoFar(TrackScope.OnThisComputer).Measured);

        _db.SaveTrackMood("b.flac", 100, 1, new Analysis.MoodReading(0.4, 0.4, 0.4));

        Assert.Equal(2, _db.MeasuredSoFar(TrackScope.OnThisComputer).Measured);
    }

    [Fact]
    public void A_record_that_is_only_in_the_cloud_is_not_work_this_computer_can_do()
    {
        Add("here.flac", measured: true);
        Add("up-there.flac", measured: false, state: CloudState.CloudOnly);

        var progress = _db.MeasuredSoFar(TrackScope.OnThisComputer);

        Assert.Equal(1, progress.Measured);
        Assert.Equal(1, progress.Total);
        Assert.True(progress.Enough);
    }

    /// <summary>Asked about the whole collection it counts those rows, which is the other true
    /// answer. Both are offered because the scope is the caller's to state.</summary>
    [Fact]
    public void Asked_about_everything_the_cloud_rows_are_counted()
    {
        Add("here.flac", measured: true);
        Add("up-there.flac", measured: false, state: CloudState.CloudOnly);

        var progress = _db.MeasuredSoFar(TrackScope.Anywhere);

        Assert.Equal(1, progress.Measured);
        Assert.Equal(2, progress.Total);
    }

    /// <summary>A library of nothing but cloud records has no measuring to do, and a gate waiting on
    /// a pass with nothing to measure would never open. <see cref="MeasuringProgress.Enough"/> reads
    /// a total of nought as "finished", which is why this is safe rather than a divide by nought.
    /// </summary>
    [Fact]
    public void A_shelf_of_nothing_but_cloud_records_has_no_pass_to_wait_for()
    {
        Add("up-there.flac", measured: false, state: CloudState.CloudOnly);

        var progress = _db.MeasuredSoFar(TrackScope.OnThisComputer);

        Assert.Equal(0, progress.Total);
        Assert.True(progress.Enough);
    }
}

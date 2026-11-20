using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// The generated lists are cut on the listener's clock, which is not always the clock of the machine that
/// cuts them. The store cuts lists for a phone in another time zone, and "Your mix" must lean towards what
/// that person plays at their own hour. See <see cref="LibraryDatabase.ListenFrom"/>.
/// </summary>
public class ListenerZoneTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    /// <summary>11:00 UTC. That is 23:00 at twelve hours ahead of UTC (night) and 09:00 at two hours behind
    /// it (morning).</summary>
    private static readonly long Now = new DateTime(2026, 8, 20, 11, 0, 0, DateTimeKind.Utc).Ticks;

    private static readonly TimeZoneInfo Ahead =
        TimeZoneInfo.CreateCustomTimeZone("rpm-test+12", TimeSpan.FromHours(12), "rpm-test+12", "rpm-test+12");

    private static readonly TimeZoneInfo Behind =
        TimeZoneInfo.CreateCustomTimeZone("rpm-test-2", TimeSpan.FromHours(-2), "rpm-test-2", "rpm-test-2");

    public ListenerZoneTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmzone-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private void Add(string folder, string genre)
    {
        var path = $"{folder}/01.flac";
        _db.Upsert(new LibraryTrack
        {
            RelativePath = path,
            Folder = folder,
            FileName = "01.flac",
            Extension = ".flac",
            SizeBytes = 5_000_000,
            ModifiedUtcTicks = 1,
            DurationSeconds = 200,
            Title = folder,
            Album = folder,
            Artist = folder,
            Genre = genre,
        });
    }

    /// <summary>Three jazz records played late at night and three metal records played in the morning, each
    /// on the listener's own clock. The plays are the same, so only the hour can tell them apart.</summary>
    private void JazzAtNightMetalInTheMorning()
    {
        for (int i = 0; i < 3; i++)
        {
            Add($"jazz{i}", "Jazz");
            Add($"metal{i}", "Metal");
        }

        for (int day = 1; day <= 3; day++)
            for (int i = 0; i < 3; i++)
            {
                var night = new DateTimeOffset(2026, 8, day, 23, 10 + i, 0, TimeSpan.FromHours(1));
                var morning = new DateTimeOffset(2026, 8, day, 9, 10 + i, 0, TimeSpan.FromHours(1));
                Assert.True(_db.NoteListen(ListenEntry.At(night, $"jazz{i}/01.flac", 200, 200)!));
                Assert.True(_db.NoteListen(ListenEntry.At(morning, $"metal{i}/01.flac", 200, 200)!));
            }
    }

    private string FirstOfYourMix() =>
        _db.QuerySmartPlaylist(SmartPlaylistId.WholeShelf(SmartPlaylistKind.RightNow), Now, advance: false)[0].Folder;

    [Fact]
    public void Your_mix_leans_on_the_listeners_hour_ahead_of_utc()
    {
        JazzAtNightMetalInTheMorning();
        _db.ListenFrom(Ahead);

        Assert.StartsWith("jazz", FirstOfYourMix());
    }

    [Fact]
    public void Your_mix_leans_on_the_listeners_hour_behind_utc()
    {
        JazzAtNightMetalInTheMorning();
        _db.ListenFrom(Behind);

        Assert.StartsWith("metal", FirstOfYourMix());
    }

    [Fact]
    public void The_zone_is_this_machines_until_somebody_says_otherwise()
    {
        Assert.Equal(TimeZoneInfo.Local, _db.ListenerZone);
    }

    /// <summary>The cut is held for the session. A zone changed after it would leave the lists cut on one
    /// clock and the hour read on another.</summary>
    [Fact]
    public void The_zone_cannot_change_once_the_session_is_cut()
    {
        _ = _db.SessionMoods;

        Assert.Throws<InvalidOperationException>(() => _db.ListenFrom(Ahead));
    }
}

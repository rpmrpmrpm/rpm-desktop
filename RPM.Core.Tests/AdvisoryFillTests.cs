using RPM.Core.Analysis;
using RPM.Core.Library;
using RPM.Core.Metadata;
using RPM.Core.Metadata.Providers;
using RPM.Core.Models;

namespace RPM.Core.Tests;

/// <summary>
/// The explicit flag a record's songs take from the release the catalogue matched: which listing track a song is,
/// what may be changed (no flag, and Clean only on a strong match), and that nothing is ever made Clean or taken
/// away from Explicit.
/// </summary>
public class AdvisoryFillTests : IDisposable
{
    private const ContentAdvisory E = ContentAdvisory.Explicit;
    private const ContentAdvisory C = ContentAdvisory.Clean;
    private const ContentAdvisory U = ContentAdvisory.Unknown;

    private readonly string _root;

    public AdvisoryFillTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"advfill-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static ReleaseTrackInfo T(uint position, string title, double seconds, ContentAdvisory advisory)
        => new(position, title, "Kendrick Lamar", seconds, Advisory: advisory);

    private static ReleaseDetail Release(params ReleaseTrackInfo[] tracks)
        => new(new ReleaseRef(RpmMetadataProvider.ProviderId, "r1"), "DAMN.", "Kendrick Lamar", 2017, "US", "CD", tracks);

    private static readonly ReleaseDetail Damn = Release(
        T(1, "BLOOD.", 118, U),
        T(2, "DNA.", 185, E),
        T(3, "YAH.", 160, E),
        T(4, "ELEMENT.", 208, E));

    private static AdvisoryFill.Song Song(string title, double seconds = 0, uint track = 0, ContentAdvisory advisory = U)
        => new(title, seconds, 0, track, advisory);

    // ── a song with no flag ──

    [Fact]
    public void A_song_with_no_flag_takes_explicit_from_the_track_of_its_name()
        => Assert.Equal(E, AdvisoryFill.Decide(Song("DNA.", 186), Damn, 4));

    [Fact]
    public void Case_accents_a_featured_credit_and_a_note_in_brackets_do_not_stop_the_match()
    {
        Assert.Equal(E, AdvisoryFill.Decide(Song("dna"), Damn, 4));
        Assert.Equal(E, AdvisoryFill.Decide(Song("DNA. (feat. Rihanna) [Remastered]"), Damn, 4));
        Assert.Equal(E, AdvisoryFill.Decide(Song("DNA. " + ExplicitTitleMark.Mark), Damn, 4));
    }

    [Fact]
    public void A_length_too_far_from_the_track_is_another_recording_and_is_left_alone()
        => Assert.Null(AdvisoryFill.Decide(Song("DNA.", 185 + AdvisoryFill.NearSeconds + 1), Damn, 4));

    [Fact]
    public void A_track_the_listing_does_not_call_explicit_gives_no_answer()
    {
        Assert.Null(AdvisoryFill.Decide(Song("BLOOD."), Damn, 4));
        Assert.Null(AdvisoryFill.Decide(Song("LOYALTY."), Damn, 4));
    }

    [Fact]
    public void Several_tracks_of_one_name_answer_by_place_or_only_when_they_agree()
    {
        var record = Release(T(1, "Intro", 60, E), T(2, "Song", 200, U), T(3, "Intro", 60, U));

        Assert.Equal(E, AdvisoryFill.Decide(Song("Intro", track: 1), record, 3));
        Assert.Null(AdvisoryFill.Decide(Song("Intro", track: 3), record, 3));
        Assert.Null(AdvisoryFill.Decide(Song("Intro"), record, 3));
    }

    // ── what may be changed ──

    [Fact]
    public void Explicit_is_never_changed_and_nothing_is_ever_made_clean()
    {
        Assert.Null(AdvisoryFill.Decide(Song("BLOOD.", advisory: E), Damn, 4));

        var cleaned = Release(T(1, "DNA.", 185, C));
        Assert.Null(AdvisoryFill.Decide(Song("DNA."), cleaned, 1));
    }

    [Fact]
    public void Clean_becomes_explicit_only_on_a_strong_match()
    {
        // Same place, both lengths known and close, and as many tracks as the record has songs.
        Assert.Equal(E, AdvisoryFill.Decide(Song("DNA.", 186, track: 2, advisory: C), Damn, 4));

        // Any one of those missing is not enough.
        Assert.Null(AdvisoryFill.Decide(Song("DNA.", 186, track: 3, advisory: C), Damn, 4));
        Assert.Null(AdvisoryFill.Decide(Song("DNA.", 0, track: 2, advisory: C), Damn, 4));
        Assert.Null(AdvisoryFill.Decide(Song("DNA.", 185 + AdvisoryFill.StrongSeconds + 1, track: 2, advisory: C), Damn, 4));
        Assert.Null(AdvisoryFill.Decide(Song("DNA.", 186, track: 2, advisory: C), Damn, 5));
    }

    [Fact]
    public void A_queued_file_whose_flag_was_set_in_the_queue_is_left_alone()
    {
        var current = new TrackInfo { FilePath = "/q/2.flac", Extension = ".flac", Title = "DNA.", Advisory = U };
        var picked = current with { Advisory = C };

        Assert.Same(picked, AdvisoryFill.Fill(picked, current, Damn, 4));

        var filled = AdvisoryFill.Fill(current, current, Damn, 4);
        Assert.Equal(E, filled.Advisory);
        Assert.True(filled.TagsEdited);
    }

    // ── records already in the library ──

    private static LibraryTrack Row(string title, uint track, double seconds, ContentAdvisory? advisory = null)
        => new()
        {
            RelativePath = $"Kendrick Lamar/DAMN/{track:00} {title}.mp3",
            Folder = "Kendrick Lamar/DAMN",
            FileName = $"{track:00} {title}.mp3",
            Extension = ".mp3",
            Title = title,
            Artist = "Kendrick Lamar",
            AlbumArtist = "Kendrick Lamar",
            Album = "DAMN.",
            TrackNumber = track,
            DurationSeconds = seconds,
            Advisory = advisory,
        };

    [Fact]
    public void A_plan_names_only_the_songs_that_change_and_leaves_the_ones_it_is_told_to()
    {
        var rows = new[] { Row("BLOOD.", 1, 118), Row("DNA.", 2, 185), Row("YAH.", 3, 160), Row("ELEMENT.", 4, 208, E) };

        var plan = AdvisoryFill.Plan(rows, Damn, new HashSet<string> { rows[2].RelativePath });

        var change = Assert.Single(plan);
        Assert.Equal("DNA.", change.Row.Title);
        Assert.Equal(U, change.Was);
        Assert.Equal(E, change.Becomes);
    }

    [Fact]
    public void A_record_whose_songs_are_all_flagged_explicit_is_not_asked_about()
    {
        Assert.Null(AdvisoryFill.AlbumOf([Row("DNA.", 2, 185, E)], null));

        var album = AdvisoryFill.AlbumOf([Row("DNA.", 2, 185), Row("YAH.", 3, 160, E)], "mb1");
        Assert.NotNull(album);
        Assert.Equal("Kendrick Lamar", album!.Artist);
        Assert.Equal(2, album.TrackCount);
        Assert.Equal("mb1", album.ReleaseId);
    }

    [Fact]
    public void Write_puts_the_flag_in_the_file_and_the_row_and_marks_the_row_edited()
    {
        var db = new LibraryDatabase(MirrorLocation.InFolder(_root));
        try
        {
            var row = Mp3("Kendrick Lamar/DAMN/02 DNA.mp3", "DNA.");
            db.UpsertMany([row]);
            var stored = db.FindByRelativePath(row.RelativePath)!;

            var written = AdvisoryFill.Write(db, _root, [new AdvisoryFill.Change(stored, U, E)]);

            Assert.Equal(1, written);
            Assert.Equal(E, TagReader.Read(Path.Combine(_root, "Kendrick Lamar", "DAMN", "02 DNA.mp3")).Advisory);
            var after = db.FindByRelativePath(row.RelativePath)!;
            Assert.Equal(E, after.Advisory);
            Assert.True(after.MetadataEditedUtcTicks > 0);
        }
        finally
        {
            db.Dispose();
        }
    }

    [Fact]
    public void Write_leaves_a_file_whose_flag_changed_since_the_plan_and_one_in_use()
    {
        var db = new LibraryDatabase(MirrorLocation.InFolder(_root));
        try
        {
            var since = Mp3("A/B/01 One.mp3", "One", C);
            var busy = Mp3("A/B/02 Two.mp3", "Two");
            db.UpsertMany([since, busy]);

            var written = AdvisoryFill.Write(
                db, _root,
                [new AdvisoryFill.Change(db.FindByRelativePath(since.RelativePath)!, U, E),
                 new AdvisoryFill.Change(db.FindByRelativePath(busy.RelativePath)!, U, E)],
                busy: path => path.EndsWith("02 Two.mp3", StringComparison.Ordinal));

            Assert.Equal(0, written);
            Assert.Equal(C, TagReader.Read(Path.Combine(_root, "A", "B", "01 One.mp3")).Advisory);
            Assert.Equal(U, TagReader.Read(Path.Combine(_root, "A", "B", "02 Two.mp3")).Advisory);
        }
        finally
        {
            db.Dispose();
        }
    }

    /// <summary>A real, tiny MP3 at <paramref name="relative"/>, tagged, and its library row.</summary>
    private LibraryTrack Mp3(string relative, string title, ContentAdvisory advisory = U)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
        File.WriteAllBytes(path, frame);
        TagReader.WriteAll(path, new TrackInfo
        {
            FilePath = path, Extension = ".mp3", Title = title, Artist = "Kendrick Lamar", Album = "DAMN.",
            Advisory = advisory,
        });
        return LibraryDatabase.FromTrackInfo(TagReader.Read(path), _root);
    }
}

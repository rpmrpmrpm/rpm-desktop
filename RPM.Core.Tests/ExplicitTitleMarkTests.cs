using RPM.Core.Library;
using RPM.Core.Metadata;
using RPM.Core.Models;

namespace RPM.Core.Tests;

/// <summary>
/// Some download tools add the explicit mark to the end of a song title ("Killah 🅴"). rpm draws its own
/// explicit badge from the advisory flag, so a title that keeps the mark shows two marks in two styles.
/// The mark is taken off the title when the file is read and when the row is written, and it counts as
/// the advisory flag when the track has none.
/// </summary>
public class ExplicitTitleMarkTests : IDisposable
{
    private const string Mark = "\U0001F174";

    private readonly string _root;

    public ExplicitTitleMarkTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmmark-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── the rule ──

    [Theory]
    [InlineData("Killah " + Mark, "Killah")]
    [InlineData("How Come You Don't Call Me " + Mark, "How Come You Don't Call Me")]
    [InlineData("Manchild " + Mark + "  ", "Manchild")]
    public void A_mark_at_the_end_of_the_title_is_taken_off(string title, string expected)
    {
        var (clean, marked) = ExplicitTitleMark.Split(title);

        Assert.Equal(expected, clean);
        Assert.True(marked);
    }

    /// <summary>The tools put the mark at the end, after a space. Anywhere else it may be part of the
    /// name, so it is left alone.</summary>
    [Theory]
    [InlineData("Killah")]
    [InlineData(Mark)]
    [InlineData("Killah" + Mark)]
    [InlineData("A " + Mark + " B")]
    [InlineData("")]
    public void A_title_without_a_mark_at_its_end_is_kept_as_it_is(string title)
    {
        var (clean, marked) = ExplicitTitleMark.Split(title);

        Assert.Equal(title, clean);
        Assert.False(marked);
    }

    [Fact]
    public void No_title_stays_no_title()
        => Assert.Equal((null, false), ExplicitTitleMark.Split(null));

    [Theory]
    [InlineData(null)]
    [InlineData(ContentAdvisory.Unknown)]
    public void A_marked_title_with_no_flag_reads_as_explicit(ContentAdvisory? flag)
        => Assert.Equal(ContentAdvisory.Explicit, ExplicitTitleMark.Advisory(marked: true, flag));

    /// <summary>A flag the file states is the file's own answer. The mark does not overrule it.</summary>
    [Theory]
    [InlineData(ContentAdvisory.Explicit)]
    [InlineData(ContentAdvisory.Clean)]
    public void A_stated_flag_is_kept(ContentAdvisory flag)
        => Assert.Equal(flag, ExplicitTitleMark.Advisory(marked: true, flag));

    [Theory]
    [InlineData(null)]
    [InlineData(ContentAdvisory.Unknown)]
    [InlineData(ContentAdvisory.Clean)]
    public void A_title_without_the_mark_changes_no_flag(ContentAdvisory? flag)
        => Assert.Equal(flag, ExplicitTitleMark.Advisory(marked: false, flag));

    // ── reading a file ──

    private string Mp3WithTitle(string title)
    {
        var path = Path.Combine(_root, $"{Guid.NewGuid():N}.mp3");
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
        File.WriteAllBytes(path, frame);

        using var file = TagLib.File.Create(path);
        file.Tag.Title = title;
        file.Save();
        return path;
    }

    [Fact]
    public void A_file_whose_title_carries_the_mark_reads_without_it_and_as_explicit()
    {
        var info = TagReader.Read(Mp3WithTitle("Killah " + Mark));

        Assert.Equal("Killah", info.Title);
        Assert.Equal(ContentAdvisory.Explicit, info.Advisory);
    }

    [Fact]
    public void A_file_without_the_mark_reads_as_it_is()
    {
        var info = TagReader.Read(Mp3WithTitle("Killah"));

        Assert.Equal("Killah", info.Title);
        Assert.Equal(ContentAdvisory.Unknown, info.Advisory);
    }

    // ── writing a row ──

    private static LibraryTrack Row(string title, ContentAdvisory? advisory) => new()
    {
        RelativePath = "Lady Gaga/MAYHEM/06 - Killah.m4a",
        Folder = "Lady Gaga/MAYHEM",
        FileName = "06 - Killah.m4a",
        Extension = ".m4a",
        SizeBytes = 1,
        ModifiedUtcTicks = 1,
        Title = title,
        Advisory = advisory,
    };

    /// <summary>
    /// Every write of a row goes through one place, so a title that arrives with the mark (from the
    /// catalogue, from another computer, from a build that keeps it) is stored without it.
    /// </summary>
    [Fact]
    public void A_row_written_with_the_mark_is_stored_without_it()
    {
        using var db = new LibraryDatabase(MirrorLocation.InFolder(_root));

        db.Upsert(Row("Killah " + Mark, ContentAdvisory.Unknown));

        var stored = Assert.Single(db.Query());
        Assert.Equal("Killah", stored.Title);
        Assert.Equal(ContentAdvisory.Explicit, stored.Advisory);
    }

    [Fact]
    public void A_row_without_the_mark_is_stored_as_it_is()
    {
        using var db = new LibraryDatabase(MirrorLocation.InFolder(_root));

        db.Upsert(Row("Killah", null));

        var stored = Assert.Single(db.Query());
        Assert.Equal("Killah", stored.Title);
        Assert.Null(stored.Advisory);
    }

    /// <summary>
    /// A library that already holds marked titles. Most of them have a file to read again, but a track
    /// that is only in the cloud has none, so the titles are cleaned where they are stored. This happens
    /// on every open, and finds nothing once they are clean.
    /// </summary>
    [Fact]
    public void A_library_that_holds_marked_titles_is_cleaned_when_it_opens()
    {
        using (var db = new LibraryDatabase(MirrorLocation.InFolder(_root)))
            db.Upsert(Row("Killah", null));

        WriteTitleDirectly("Killah " + Mark, advisory: null);

        using (var db = new LibraryDatabase(MirrorLocation.InFolder(_root)))
        {
            var stored = Assert.Single(db.Query());
            Assert.Equal("Killah", stored.Title);
            Assert.Equal(ContentAdvisory.Explicit, stored.Advisory);
        }
    }

    [Fact]
    public void Cleaning_on_open_keeps_a_flag_the_row_already_has()
    {
        using (var db = new LibraryDatabase(MirrorLocation.InFolder(_root)))
            db.Upsert(Row("Killah", null));

        WriteTitleDirectly("Killah " + Mark, advisory: (int)ContentAdvisory.Clean);

        using (var db = new LibraryDatabase(MirrorLocation.InFolder(_root)))
        {
            var stored = Assert.Single(db.Query());
            Assert.Equal("Killah", stored.Title);
            Assert.Equal(ContentAdvisory.Clean, stored.Advisory);
        }
    }

    /// <summary>
    /// The clean-up on open reads the marked rows through their own index and does not scan the table. The
    /// open runs on the drawing thread while the splash is up, and a scan of a large table whose pages are
    /// not in the file cache costs a few hundred milliseconds. SQLite uses a partial index only when the
    /// query states the index's own condition, so the two must not drift apart.
    /// </summary>
    [Fact]
    public void The_marked_titles_are_found_through_their_index()
    {
        using (var db = new LibraryDatabase(MirrorLocation.InFolder(_root)))
            db.Upsert(Row("Killah", null));

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(_root, ".rpm", "library.db")};Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"EXPLAIN QUERY PLAN SELECT id, title, advisory FROM tracks WHERE {LibraryDatabase.MarkedTitle}";
        using var reader = cmd.ExecuteReader();
        var plan = new List<string>();
        while (reader.Read())
            plan.Add(reader.GetString(3));

        Assert.Contains(plan, step => step.Contains("idx_tracks_title_marked", StringComparison.Ordinal));
    }

    /// <summary>Puts a title in the file the way an older build wrote it, past the rule.</summary>
    private void WriteTitleDirectly(string title, int? advisory)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(_root, ".rpm", "library.db")};Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE tracks SET title = $t, advisory = $a";
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$a", (object?)advisory ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}

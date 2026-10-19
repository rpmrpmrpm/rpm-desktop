using RPM.Core.Models;
using RPM.Desktop.Presentation.Import;
using RPM.Desktop.Presentation.Main.LeftPane.Models;

namespace RPM.Desktop.Tests;

/// <summary>
/// Which record a set of ticked rows is on. The tag panel asks this before it offers a disc label at all.
///
/// <para>A disc label is not a field of a file. It is written once, into the folder the record's files land
/// in. So the panel offers it only when the ticks cover a whole record. This test checks the first half of
/// that: all the ticks are on ONE record. The store's own rule (<c>LibraryDatabase.QueueGroupKeys</c>) says
/// which record a row is on. The test asks that rule rather than guessing again, so the panel and the tree
/// never disagree about where an album starts.</para>
/// </summary>
public class QueueRecordSelectionTests
{
    private static TrackRow Row(string? albumArtist, string? artist, string? album) =>
        new(new TrackInfo { FilePath = $"/m/{Guid.NewGuid():n}.flac", Extension = ".flac" })
        {
            AlbumArtist = albumArtist ?? "",
            Artist = artist ?? "",
            Album = album ?? "",
        };

    [Fact]
    public void Rows_from_one_record_name_it()
    {
        var got = QueueRecordSelection.RecordOf(
            [Row("Pink Floyd", "Pink Floyd", "Animals"), Row("Pink Floyd", "Pink Floyd", "Animals")]);

        Assert.Equal(("Pink Floyd", "Animals"), got);
    }

    /// <summary>The album artist is what a record is filed under, so a compilation whose tracks each
    /// carry their own performer is still one record.</summary>
    [Fact]
    public void A_compilation_is_one_record_however_many_performers_are_on_it()
    {
        var got = QueueRecordSelection.RecordOf(
            [Row("Various Artists", "Blondie", "Now 12"), Row("Various Artists", "The Jam", "Now 12")]);

        Assert.Equal(("Various Artists", "Now 12"), got);
    }

    /// <summary>Spelling is not the question: the store groups case-insensitively, so a record spelled
    /// two ways is one node in the tree and has to be one record here.</summary>
    [Fact]
    public void One_record_spelled_two_ways_is_still_one_record()
        => Assert.NotNull(QueueRecordSelection.RecordOf(
            [Row("Pink Floyd", null, "Animals"), Row("pink floyd", null, "ANIMALS")]));

    [Fact]
    public void Rows_from_two_records_are_on_no_one_record()
        => Assert.Null(QueueRecordSelection.RecordOf(
            [Row("Pink Floyd", null, "Animals"), Row("Pink Floyd", null, "Meddle")]));

    [Fact]
    public void Rows_from_two_artists_are_on_no_one_record()
        => Assert.Null(QueueRecordSelection.RecordOf(
            [Row("Pink Floyd", null, "Animals"), Row("Wire", null, "Animals")]));

    /// <summary>Nothing ticked is not a record, and the panel offers nothing for it.</summary>
    [Fact]
    public void Nothing_ticked_is_no_record()
        => Assert.Null(QueueRecordSelection.RecordOf([]));

    /// <summary>The unknown buckets are real records. An album with no name is where a disc label is most
    /// likely to be wanted, such as a rip nobody has identified yet.</summary>
    [Fact]
    public void Untagged_rows_are_a_record_too()
        => Assert.Equal(("", ""), QueueRecordSelection.RecordOf([Row(null, null, null), Row("", "", "")]));
}

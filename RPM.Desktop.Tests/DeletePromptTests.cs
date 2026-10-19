using RPM.Core.Cloud;
using RPM.Core.Library;
using RPM.Desktop.Presentation.Main.Library;

namespace RPM.Desktop.Tests;

/// <summary>
/// The sentence the delete dialog shows the listener. There are fifteen sentences, because the question is
/// really different in each case. One sentence that hides the differences would let someone delete their
/// only copy of something while believing it is still in the cloud.
/// </summary>
public class DeletePromptTests
{
    // ── the title and the button say what the menu said ───────────────────────────────────────────

    /// <summary>The menu entry, the dialog title and the confirm button all use one string. So the dialog
    /// clearly answers the item that was clicked.</summary>
    [Theory]
    [InlineData(DeleteReach.Local, "Delete_Local")]
    [InlineData(DeleteReach.Cloud, "Delete_Cloud")]
    [InlineData(DeleteReach.Everywhere, "Delete_Everywhere")]
    public void Each_reach_is_named_by_one_string(DeleteReach reach, string key)
    {
        Assert.Equal(key, DeletePrompt.ActionKey(reach));
        Assert.Equal(key, DeletePrompt.For(reach, DeleteSubject.Album, [CloudState.Synced]).ActionKey);
    }

    // ── delete local files ────────────────────────────────────────────────────────────────────────

    /// <summary>The gentle case: the cloud copy stays, so the record stays.</summary>
    [Fact]
    public void Trashing_the_files_of_a_record_that_is_also_in_the_cloud_says_the_record_stays()
    {
        var wording = DeletePrompt.For(DeleteReach.Local, DeleteSubject.Album, [CloudState.Synced]);

        Assert.Equal("AlbumDelete_Local", wording.BodyKey);
        Assert.False(wording.LeavesLibrary);
    }

    [Fact]
    public void Trashing_the_files_of_a_record_that_is_nowhere_else_says_it_goes()
    {
        var wording = DeletePrompt.For(DeleteReach.Local, DeleteSubject.Album, [CloudState.Local]);

        Assert.Equal("AlbumDelete_LocalLast", wording.BodyKey);
        Assert.True(wording.LeavesLibrary);
    }

    /// <summary>If eleven tracks keep their cloud copy and one does not, the delete loses a song. The
    /// gentler sentence would be true for eleven tracks and false for the twelfth. So the stronger sentence
    /// is used for the whole selection.</summary>
    [Fact]
    public void One_track_with_nowhere_else_to_be_decides_the_wording_for_the_whole_selection()
    {
        var states = Enumerable.Repeat(CloudState.Synced, 11).Append(CloudState.Local);

        var wording = DeletePrompt.For(DeleteReach.Local, DeleteSubject.Tracks, states);

        Assert.Equal("TrackDelete_ManyLocalLast", wording.BodyKey);
        Assert.True(wording.LeavesLibrary);
    }

    [Fact]
    public void A_selection_entirely_in_the_cloud_gets_the_gentle_sentence()
    {
        var wording = DeletePrompt.For(
            DeleteReach.Local, DeleteSubject.Tracks, [CloudState.Synced, CloudState.Synced]);

        Assert.Equal("TrackDelete_ManyLocal", wording.BodyKey);
        Assert.False(wording.LeavesLibrary);
    }

    // ── delete from cloud ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Deleting_the_cloud_copy_of_a_song_that_is_here_too_says_the_file_stays()
    {
        var wording = DeletePrompt.For(DeleteReach.Cloud, DeleteSubject.Track, [CloudState.Synced]);

        Assert.Equal("TrackDelete_Cloud", wording.BodyKey);
        Assert.False(wording.LeavesLibrary);
    }

    /// <summary>The song is only in the cloud, so the copy being deleted is the only one. The sentence must
    /// say that, and must not say that the file on this computer stays.</summary>
    [Fact]
    public void Deleting_the_cloud_copy_of_a_song_that_is_only_up_there_says_it_is_the_last_one()
    {
        var wording = DeletePrompt.For(DeleteReach.Cloud, DeleteSubject.Track, [CloudState.CloudOnly]);

        Assert.Equal("TrackDelete_CloudLast", wording.BodyKey);
        Assert.True(wording.LeavesLibrary);
    }

    // ── delete everywhere ─────────────────────────────────────────────────────────────────────────

    /// <summary>There is no branch here. Whatever the state of the rows, they are removed, and the sentence
    /// is always the strong one.</summary>
    [Theory]
    [InlineData(CloudState.Synced)]
    [InlineData(CloudState.CloudOnly)]
    [InlineData(CloudState.Local)]
    [InlineData(CloudState.Queued)]
    public void Deleting_everywhere_always_says_everything_goes(CloudState state)
    {
        var wording = DeletePrompt.For(DeleteReach.Everywhere, DeleteSubject.Album, [state]);

        Assert.Equal("AlbumDelete_Everywhere", wording.BodyKey);
        Assert.True(wording.LeavesLibrary);
    }

    // ── one, many, and a record ───────────────────────────────────────────────────────────────────

    /// <summary>One song is named, a selection is counted, and a record is named. The three subjects are
    /// needed because naming the first of nine songs would say the dialog deletes one song, while it
    /// deletes nine.</summary>
    [Theory]
    [InlineData(DeleteSubject.Album, "AlbumDelete_Local")]
    [InlineData(DeleteSubject.Track, "TrackDelete_Local")]
    [InlineData(DeleteSubject.Tracks, "TrackDelete_ManyLocal")]
    public void Each_subject_gets_its_own_sentence(DeleteSubject subject, string key)
        => Assert.Equal(key, DeletePrompt.For(DeleteReach.Local, subject, [CloudState.Synced]).BodyKey);

    /// <summary>Every reach and subject pair must give a key, and no two pairs may give the same key. A
    /// missing key is shown in the interface as the key name itself.</summary>
    [Fact]
    public void Every_reach_and_subject_reaches_its_own_string()
    {
        var keys = new HashSet<string>();
        foreach (var reach in Enum.GetValues<DeleteReach>())
        foreach (var subject in Enum.GetValues<DeleteSubject>())
        foreach (var state in Enum.GetValues<CloudState>())
        {
            var key = DeletePrompt.For(reach, subject, [state]).BodyKey;
            Assert.False(string.IsNullOrWhiteSpace(key));
            keys.Add(key);
        }

        // 3 subjects x (2 local + 2 cloud + 1 everywhere).
        Assert.Equal(15, keys.Count);
    }

    /// <summary>The wording and the plan must agree. The dialog says a row is removed exactly when
    /// <see cref="DeletePlan"/> removes it.</summary>
    [Fact]
    public void The_sentence_agrees_with_what_the_delete_will_actually_do()
    {
        foreach (var reach in Enum.GetValues<DeleteReach>())
        foreach (var state in Enum.GetValues<CloudState>())
        {
            var wording = DeletePrompt.For(reach, DeleteSubject.Track, [state]);
            var removes = DeletePlan.For(reach, state) == DeleteOutcome.RemoveEntirely;

            Assert.Equal(removes, wording.LeavesLibrary);
        }
    }
}

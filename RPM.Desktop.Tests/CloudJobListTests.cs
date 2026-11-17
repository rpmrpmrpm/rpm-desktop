using RPM.Core.Cloud;
using RPM.Desktop.Services.Cloud;

namespace RPM.Desktop.Tests;

/// <summary>
/// The cloud chip's flyout lists only the tracks being worked on now, and words its count line for
/// the batch's operation.
///
/// <para>A first upload of a large library touches thousands of tracks. A list of every one of them,
/// most reading "Done", tells the user nothing. The count line says how many are done; the list says
/// what is happening at this moment.</para>
/// </summary>
public class CloudJobListTests
{
    private static CloudJobRow Row(long id = 1) => new(id, CloudOp.Upload, "Track", "Album");

    [Fact]
    public void A_track_waiting_for_its_turn_is_not_listed()
    {
        var working = new List<CloudJobRow>();
        var row = Row();

        CloudJobService.Place(working, row);

        Assert.Empty(working);
    }

    [Theory]
    [InlineData(CloudStage.Transcoding)]
    [InlineData(CloudStage.Uploading)]
    [InlineData(CloudStage.Verifying)]
    [InlineData(CloudStage.Removing)]
    [InlineData(CloudStage.Restoring)]
    public void A_track_being_worked_on_is_listed(CloudStage stage)
    {
        var working = new List<CloudJobRow>();
        var row = Row();
        row.Stage = stage;

        CloudJobService.Place(working, row);

        Assert.Equal([row], working);
    }

    [Theory]
    [InlineData(CloudStage.Done)]
    [InlineData(CloudStage.Failed)]
    public void A_track_that_is_over_leaves_the_list(CloudStage stage)
    {
        var working = new List<CloudJobRow>();
        var row = Row();
        row.Stage = CloudStage.Uploading;
        CloudJobService.Place(working, row);

        row.Stage = stage;
        CloudJobService.Place(working, row);

        Assert.Empty(working);
    }

    /// <summary>Each stage report places the row again. A row moving from one working stage to the next
    /// stays listed once.</summary>
    [Fact]
    public void A_track_moving_between_working_stages_is_listed_once()
    {
        var working = new List<CloudJobRow>();
        var row = Row();

        foreach (var stage in new[] { CloudStage.Transcoding, CloudStage.Verifying, CloudStage.Uploading })
        {
            row.Stage = stage;
            CloudJobService.Place(working, row);
        }

        Assert.Equal([row], working);
    }

    /// <summary>A retry puts the row back to Waiting. It leaves the list until a worker picks it up.</summary>
    [Fact]
    public void A_track_taken_up_again_leaves_the_list_until_it_starts()
    {
        var working = new List<CloudJobRow>();
        var row = Row();
        row.Stage = CloudStage.Uploading;
        CloudJobService.Place(working, row);

        row.Stage = CloudStage.Waiting;
        CloudJobService.Place(working, row);

        Assert.Empty(working);
    }

    /// <summary>The list keeps the order the tracks started in, whatever order their reports arrive in.</summary>
    [Fact]
    public void Tracks_are_listed_in_the_order_they_started()
    {
        var working = new List<CloudJobRow>();
        var a = Row(1);
        var b = Row(2);
        var c = Row(3);

        foreach (var row in new[] { a, b, c })
        {
            row.Stage = CloudStage.Transcoding;
            CloudJobService.Place(working, row);
        }
        b.Stage = CloudStage.Uploading;
        CloudJobService.Place(working, b);

        Assert.Equal([a, b, c], working);
    }

    [Theory]
    [InlineData(CloudOp.Upload, 40, "CloudChip_CountUpload")]
    [InlineData(CloudOp.Restore, 12, "CloudChip_CountRestore")]
    [InlineData(CloudOp.Remove, 16, "CloudChip_CountRemove")]
    [InlineData(CloudOp.Offload, 5, "CloudChip_CountDone")]
    public void The_count_line_names_the_batch_operation(CloudOp op, int total, string key)
        => Assert.Equal(key, CloudJobService.CountKey(op, total));

    /// <summary>A removal started during the upload run: neither word fits every track.</summary>
    [Fact]
    public void A_batch_with_two_operations_is_counted_as_done()
        => Assert.Equal("CloudChip_CountDone", CloudJobService.CountKey(null, 40));

    [Theory]
    [InlineData(CloudOp.Upload, "CloudChip_CountUploadOne")]
    [InlineData(CloudOp.Restore, "CloudChip_CountRestoreOne")]
    [InlineData(CloudOp.Remove, "CloudChip_CountRemoveOne")]
    [InlineData(null, "CloudChip_CountDoneOne")]
    public void A_batch_of_one_track_takes_the_singular(CloudOp? op, string key)
        => Assert.Equal(key, CloudJobService.CountKey(op, 1));

    /// <summary>A key that is missing from the table shows as its own name on screen.</summary>
    [Fact]
    public void Every_count_key_is_in_the_string_table()
    {
        var table = File.ReadAllText(Path.Combine(RepoRoot(), "RPM.Desktop", "Strings", "en", "Resources.resw"));
        CloudOp?[] ops = [CloudOp.Upload, CloudOp.Restore, CloudOp.Remove, CloudOp.Offload, null];

        foreach (var op in ops)
            foreach (var total in new[] { 1, 2 })
                Assert.Contains($"name=\"{CloudJobService.CountKey(op, total)}\"", table);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RPM.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("RPM.sln not found above the test output");
    }
}

using RPM.Core.Nas;
using RPM.Core.Sharing;

namespace RPM.Core.Tests;

public class NasUploadStateTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    private static NasUploadState.Snapshot Snap(string owner = "O1", string guest = "G1", string trust = "T1", string nas = "NAS") =>
        new(nas, owner, guest, trust);

    [Fact]
    public void Nothing_uploaded_yet_needs_an_upload() =>
        Assert.True(new NasUploadState().NeedsUpload(Snap(), Now));

    [Fact]
    public void Same_versions_and_trust_upload_nothing()
    {
        var state = new NasUploadState();
        state.Uploaded(Snap());

        Assert.False(state.NeedsUpload(Snap(), Now));
    }

    [Fact]
    public void A_changed_version_or_trust_list_uploads_again()
    {
        var state = new NasUploadState();
        state.Uploaded(Snap());

        Assert.True(state.NeedsUpload(Snap(owner: "O2"), Now));
        Assert.True(state.NeedsUpload(Snap(guest: "G2"), Now));
        Assert.True(state.NeedsUpload(Snap(trust: "T2"), Now));
        Assert.True(state.NeedsUpload(Snap(nas: "OTHER"), Now));
    }

    [Fact]
    public void Removing_a_device_changes_the_trust_key()
    {
        NasTrusted tv = new() { PublicKey = "TV", Kind = PeerKind.Account };
        NasTrusted friend = new() { PublicKey = "FRIEND", Kind = PeerKind.Guest };

        Assert.NotEqual(NasUploadState.TrustKey([tv, friend]), NasUploadState.TrustKey([tv]));
        Assert.Equal(NasUploadState.TrustKey([tv, friend]), NasUploadState.TrustKey([friend, tv]));
    }

    [Fact]
    public void Failures_back_off_up_to_five_minutes_and_success_resets()
    {
        var state = new NasUploadState();

        Assert.Equal(TimeSpan.FromSeconds(30), state.Failed(Now));
        Assert.False(state.NeedsUpload(Snap(), Now.AddSeconds(29)));
        Assert.True(state.NeedsUpload(Snap(), Now.AddSeconds(30)));
        Assert.Equal(TimeSpan.FromSeconds(60), state.Failed(Now));
        for (var i = 0; i < 10; i++)
            state.Failed(Now);
        Assert.Equal(TimeSpan.FromMinutes(5), state.Failed(Now));

        state.Uploaded(Snap());
        Assert.True(state.NeedsUpload(Snap(owner: "O2"), Now));
    }

    [Fact]
    public void Pictures_are_sent_once_per_stamp_and_again_for_a_new_nas()
    {
        var state = new NasUploadState();
        state.Uploaded(Snap());
        Assert.True(state.PictureNeeded(12, "A"));
        state.PictureSent(12, "A");
        Assert.False(state.PictureNeeded(12, "A"));
        Assert.True(state.PictureNeeded(12, "B"));

        state.Uploaded(Snap(nas: "OTHER"));
        Assert.True(state.PictureNeeded(12, "A"));
    }
}

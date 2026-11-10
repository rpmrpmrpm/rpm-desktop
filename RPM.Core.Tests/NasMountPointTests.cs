using RPM.Core.Nas;

namespace RPM.Core.Tests;

public class NasMountPointTests
{
    [Fact]
    public void Unmounted_mount_folder_is_never_opened_as_a_library()
    {
        // What rpm's own mount folder is when the share is not mounted: an empty folder on this disk.
        var folder = Path.Combine(Path.GetTempPath(), "rpm-mount-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Assert.False(NasMountPoint.IsLiveNetworkMount(folder));
        }
        finally
        {
            Directory.Delete(folder);
        }
    }

    [Fact]
    public void Missing_or_empty_path_is_not_a_mount()
    {
        Assert.False(NasMountPoint.IsLiveNetworkMount(""));
        Assert.False(NasMountPoint.IsLiveNetworkMount(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid())));
    }
}

public class NasSmbAddressTests
{
    [Fact]
    public void Reads_host_share_and_path()
    {
        var address = NasSmbAddress.Parse("smb://diskstation.local/music/rpm")!;

        Assert.Equal("diskstation.local", address.Host);
        Assert.Equal("music", address.Share);
        Assert.Equal("rpm", address.Path);
        Assert.Equal("smb://diskstation.local/music", address.ShareUrl);
        Assert.Equal(@"\\diskstation.local\music", address.UncShare);
    }

    [Fact]
    public void Reads_a_share_without_a_path_and_a_deeper_path()
    {
        Assert.Equal("", NasSmbAddress.Parse("smb://nas/music")!.Path);
        Assert.Equal("a/b c", NasSmbAddress.Parse("smb://nas/music/a/b%20c")!.Path);
    }

    [Fact]
    public void Keeps_a_port_for_the_mount_and_drops_it_for_unc()
    {
        var address = NasSmbAddress.Parse("smb://127.0.0.1:1445/Data")!;

        Assert.Equal("127.0.0.1:1445", address.Host);
        Assert.Equal("smb://127.0.0.1:1445/Data", address.ShareUrl);
        Assert.Equal(@"\\127.0.0.1\Data", address.UncShare);
    }

    [Fact]
    public void Knows_its_share_in_the_mount_table()
    {
        var address = NasSmbAddress.Parse("smb://127.0.0.1:1447/Music/rpm")!;

        Assert.True(address.IsMountedFrom("//rpm@127.0.0.1:1447/Music"));
        Assert.True(address.IsMountedFrom("//other;WORKGROUP@127.0.0.1:1447/music"));
        Assert.False(address.IsMountedFrom("//rpm@127.0.0.1:1448/Music"));
        Assert.False(address.IsMountedFrom("//rpm@127.0.0.1/Music"));
        Assert.False(address.IsMountedFrom("//rpm@127.0.0.1:1447/Musical"));
        Assert.False(address.IsMountedFrom("//rpm@localhost:1447/Music"));
        Assert.False(address.IsMountedFrom("/dev/disk3s1"));
        Assert.False(address.IsMountedFrom("map auto_home"));

        // No port means the SMB port, on either side, and the share name is read with its escapes undone.
        var plain = NasSmbAddress.Parse("smb://DiskStation.local/My%20Music")!;
        Assert.True(plain.IsMountedFrom("//chivan@diskstation.local/My%20Music"));
        Assert.True(plain.IsMountedFrom("//chivan@diskstation.local:445/my%20music"));
        Assert.False(plain.IsMountedFrom("//chivan@diskstation.local:1445/My%20Music"));
    }

    [Fact]
    public void Refuses_what_is_not_an_smb_share()
    {
        Assert.Null(NasSmbAddress.Parse(null));
        Assert.Null(NasSmbAddress.Parse("http://nas/music"));
        Assert.Null(NasSmbAddress.Parse("smb://nas"));
        Assert.Null(NasSmbAddress.Parse("not a url"));
    }

    [Fact]
    public void Library_path_joins_the_path_and_the_folder()
    {
        var address = NasSmbAddress.Parse("smb://nas/music/rpm")!;

        Assert.Equal(Path.Combine("/mnt", "rpm", "Chivan"), address.LibraryPath("/mnt", "Chivan"));
    }
}

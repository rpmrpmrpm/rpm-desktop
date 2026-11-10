using System.Net;
using System.Net.Sockets;
using RPM.Core.Nas;
using RPM.Desktop.Services.Account;
using RPM.Desktop.Services.Nas;

namespace RPM.Desktop.Tests;

public sealed class NasStartupTests : IDisposable
{
    private readonly TcpListener _smbPort = new(IPAddress.Loopback, 0);
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "rpm-nas-start-" + Guid.NewGuid().ToString("N"));

    public NasStartupTests()
    {
        // Something listening, so the reachability check passes and the fake mount is reached.
        _smbPort.Start();
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        _smbPort.Stop();
        try { Directory.Delete(_temp, recursive: true); } catch (IOException) { }
    }

    private NasLocation Location() =>
        new("NASID", "DiskStation", $"smb://127.0.0.1:{((IPEndPoint)_smbPort.LocalEndpoint).Port}/music/rpm", "Chivan");

    /// <summary>Says the share is mounted at a folder on this disk, as an unmounted mount folder is.</summary>
    private sealed class LocalFolderMount(string root) : NasMount
    {
        public int Calls;

        protected override NasMountResult MountCore(NasSmbAddress address, NasCredentials login)
        {
            Calls++;
            return new NasMountResult.Mounted(root);
        }
    }

    private sealed class RefusingMount : NasMount
    {
        protected override NasMountResult MountCore(NasSmbAddress address, NasCredentials login) =>
            new NasMountResult.WrongLogin();
    }

    [Fact]
    public void A_mount_that_gives_a_local_folder_is_not_opened()
    {
        // Even with the library folder present inside it, a folder on this disk is not the NAS.
        Directory.CreateDirectory(Path.Combine(_temp, "rpm", "Chivan"));
        var mount = new LocalFolderMount(_temp);

        Assert.Null(NasStartup.Resolve(Location(), new NasCredentials("chivan", "secret"), mount));
        Assert.Equal(1, mount.Calls);
    }

    [Fact]
    public void No_stored_login_mounts_nothing()
    {
        var mount = new LocalFolderMount(_temp);

        Assert.Null(NasStartup.Resolve(Location(), null, mount));
        Assert.Equal(0, mount.Calls);
    }

    [Fact]
    public void A_refused_login_opens_nothing() =>
        Assert.Null(NasStartup.Resolve(Location(), new NasCredentials("chivan", "wrong"), new RefusingMount()));

    [Fact]
    public void An_unreachable_nas_is_not_mounted()
    {
        var port = ((IPEndPoint)_smbPort.LocalEndpoint).Port;
        _smbPort.Stop();
        var mount = new LocalFolderMount(_temp);

        Assert.IsType<NasMountResult.Unreachable>(
            mount.Mount(NasSmbAddress.Parse($"smb://127.0.0.1:{port}/music")!, new NasCredentials("a", "b")));
        Assert.Equal(0, mount.Calls);
    }
}

public class NasLoginTests
{
    [Fact]
    public void Login_round_trips_through_the_secret_store()
    {
        var login = new NasLogin(new MemorySecretStore());

        Assert.True(login.Save("NASID", new NasCredentials("chivan", "s3cret")));

        Assert.Equal(new NasCredentials("chivan", "s3cret"), login.Read("NASID"));
        Assert.Null(login.Read("OTHER"));
    }

    [Fact]
    public void The_password_is_not_in_the_printed_record() =>
        Assert.DoesNotContain("s3cret", new NasCredentials("chivan", "s3cret").ToString(), StringComparison.Ordinal);
}

using System;
using System.IO;
using RPM.Core.Sharing;
using RPM.Desktop.Services.Sharing;
using Xunit;

namespace RPM.Desktop.Tests;

/// <summary>
/// This install's sharing key is what every paired computer trusts, so it is replaced only when there is
/// no key to keep. A read that fails must never write a new key over the old one.
/// </summary>
public class PeerKeyStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rpm-peerkey").FullName;

    private string KeyPath => Path.Combine(_dir, "peer.key");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void No_file_makes_a_key_and_writes_it()
    {
        var made = PeerKeyStore.LoadOrMake(KeyPath);

        Assert.True(File.Exists(KeyPath));
        Assert.Equal(made.PublicKey, PeerIdentity.Import(File.ReadAllBytes(KeyPath)).PublicKey);
    }

    [Fact]
    public void A_saved_key_is_read_back()
    {
        var saved = PeerIdentity.Create();
        File.WriteAllBytes(KeyPath, saved.Export());

        Assert.Equal(saved.PublicKey, PeerKeyStore.LoadOrMake(KeyPath).PublicKey);
    }

    /// <summary>A key file that exists but cannot be read this time (here: no read permission) is left
    /// exactly as it is.</summary>
    [Fact]
    public void A_file_that_cannot_be_read_is_not_overwritten()
    {
        if (OperatingSystem.IsWindows())
            return;

        var saved = PeerIdentity.Create();
        File.WriteAllBytes(KeyPath, saved.Export());
        File.SetUnixFileMode(KeyPath, UnixFileMode.None);

        try
        {
            var session = PeerKeyStore.LoadOrMake(KeyPath);
            Assert.NotEqual(saved.PublicKey, session.PublicKey);
        }
        finally
        {
            File.SetUnixFileMode(KeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        Assert.Equal(saved.PublicKey, PeerIdentity.Import(File.ReadAllBytes(KeyPath)).PublicKey);
    }

    [Fact]
    public void A_file_that_is_not_a_key_is_kept_aside_and_replaced()
    {
        File.WriteAllBytes(KeyPath, [1, 2, 3]);

        var made = PeerKeyStore.LoadOrMake(KeyPath);

        Assert.Equal([1, 2, 3], File.ReadAllBytes(KeyPath + ".unreadable"));
        Assert.Equal(made.PublicKey, PeerIdentity.Import(File.ReadAllBytes(KeyPath)).PublicKey);
    }
}

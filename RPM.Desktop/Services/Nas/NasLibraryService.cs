using RPM.Core.Nas;
using RPM.Core.Sharing;
using RPM.Desktop.Services.Account;
using RPM.Desktop.Services.Library;
using RPM.Desktop.Services.Sharing;
using RPM.Desktop.Services.Shell;

namespace RPM.Desktop.Services.Nas;

/// <summary>What came of opening a library on a NAS.</summary>
public abstract record NasOpenResult
{
    private NasOpenResult() { }

    public sealed record Opened(string Path) : NasOpenResult;

    public sealed record WrongLogin : NasOpenResult;

    public sealed record Unreachable : NasOpenResult;

    public sealed record Failed(string Detail) : NasOpenResult;
}

/// <summary>A library on a NAS that is mounted and claimed, ready to open.</summary>
public sealed record NasPrepared(string Path, NasLocation Location, NasCredentials Login);

/// <summary>
/// Puts the library on a NAS: mounts the share with the NAS login, opens the account's library there or
/// makes a new one, writes the library id and this computer's claim, and only then opens it and saves it
/// as the library.
/// </summary>
public sealed class NasLibraryService(
    LibraryService library, SettingsService settings, AuthService auth, NasPublisher publisher)
{
    /// <summary>The NAS's own hello: its SMB address, its space and its libraries. Null when it does not
    /// answer.</summary>
    public static async Task<PeerHello?> HelloAsync(DiscoveredNas nas, CancellationToken cancellation)
    {
        using var client = new PeerClient(PeerKeyStore.Identity, nas.Host, nas.Port, nas.PublicKey, () => "");
        var hello = await client.HelloAsync(cancellation);
        return hello?.Nas is null ? null : hello;
    }

    /// <summary>The account fingerprint for this computer's signed-in account, or null when signed out.</summary>
    public string? AccountFingerprint() =>
        auth.UserId is { Length: > 0 } id ? NasClaim.AccountFingerprint(id) : null;

    /// <summary>The library this computer would open on the NAS, or null for a new one.</summary>
    public NasLibraryEntry? DefaultChoice(NasHello hello) => NasLibraryChoice.Choose(hello, AccountFingerprint());

    /// <summary>The folder a new library on this NAS would get.</summary>
    public string NewFolderName(NasHello hello) =>
        NasLibraryChoice.NewFolderName(settings.ShareName, [.. hello.Libraries.Select(l => l.Folder)]);

    /// <summary>
    /// Mounts, chooses or makes the library folder, and claims it. Reaches the network and the share, so
    /// it runs off the interface thread. Nothing is saved and nothing is opened.
    /// </summary>
    /// <param name="chosen">A library already on the NAS, or null to make a new one.</param>
    public async Task<(NasOpenResult? Problem, NasPrepared? Ready)> PrepareAsync(
        DiscoveredNas nas, PeerHello hello, NasCredentials login, NasLibraryEntry? chosen)
    {
        var smb = hello.Nas!.Smb;
        if (NasSmbAddress.Parse(smb) is not { } address)
            return (new NasOpenResult.Failed("the NAS gave no SMB address"), null);

        var newFolder = chosen is null ? NewFolderName(hello.Nas) : null;
        var name = settings.ShareName;
        var account = AccountFingerprint() ?? "";
        return await Task.Run<(NasOpenResult?, NasPrepared?)>(() =>
        {
            var mounted = NasMount.ForThisSystem().Mount(nas.DeviceId, address, login);
            switch (mounted)
            {
                case NasMountResult.WrongLogin:
                    return (new NasOpenResult.WrongLogin(), null);
                case NasMountResult.Unreachable:
                    return (new NasOpenResult.Unreachable(), null);
            }

            var root = ((NasMountResult.Mounted)mounted).ShareRoot;
            var folder = chosen?.Folder ?? newFolder!;
            var path = address.LibraryPath(root, folder);
            try
            {
                Directory.CreateDirectory(path);
                if (!NasMountPoint.IsLiveNetworkMount(path))
                    return (new NasOpenResult.Failed("the folder is not on the NAS"), null);
                NasLibrary.EnsureId(path);
                NasClaim.AddKey(path, new NasClaimKey
                {
                    PublicKey = SharingProtocol.ToBase64Url(PeerKeyStore.Identity.PublicKey),
                    Name = chosen?.Name is { Length: > 0 } existing ? existing : name,
                    Account = account,
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return (new NasOpenResult.Failed(ex.Message), null);
            }

            return (null, new NasPrepared(path, new NasLocation(nas.DeviceId, nas.Name, smb, folder), login));
        });
    }

    /// <summary>
    /// Opens a prepared library and saves it as the library. Call on the interface thread: the open
    /// raises its events on the calling thread. The settings and the login are saved only after the
    /// open succeeded, so a library that could not be opened is never the one the next launch tries.
    /// </summary>
    public NasOpenResult Open(NasPrepared ready)
    {
        ArgumentNullException.ThrowIfNull(ready);
        try
        {
            library.Open(ready.Path);
        }
        catch (Exception ex)
        {
            RPM.Core.Diagnostics.Scope.Said("nas", $"the library on the NAS could not be opened: {ex.Message}");
            return new NasOpenResult.Failed(ex.Message);
        }

        new NasLogin(auth.Secrets).Save(ready.Location.DeviceId, ready.Login);
        settings.LibraryNas = ready.Location;
        settings.LibraryPath = ready.Path;
        publisher.Poke();
        return new NasOpenResult.Opened(ready.Path);
    }
}

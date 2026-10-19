using RPM.Core.Sharing;

namespace RPM.Desktop.Services.Sharing;

/// <summary>
/// This install's key, kept next to everything else the app remembers about itself.
///
/// <para>The key is made the first time anything asks, and never again. Other computers trust this key,
/// so losing it makes this machine a new machine to every one of them. The key is kept in the app's own
/// data folder, not in the library. It belongs to this install and not to the collection. A library on
/// a portable drive that is carried to another computer must not carry this computer's identity with
/// it.</para>
/// </summary>
public static class PeerKeyStore
{
    private static readonly object Gate = new();
    private static PeerIdentity? _identity;

    private static string Path => System.IO.Path.Combine(LocalData.Dir, "peer.key");

    /// <summary>The identity, made on the first call. One for the life of the process.</summary>
    public static PeerIdentity Identity
    {
        get
        {
            lock (Gate)
            {
                if (_identity is { } held)
                    return held;

                _identity = LoadOrMake(Path);
                return _identity;
            }
        }
    }

    /// <summary>
    /// The key at <paramref name="path"/>, or a new one.
    ///
    /// <para><b>Three cases, kept apart.</b> No file: a new key is made and written there. A file that
    /// could not be read this time, for example because it is locked or the disk failed: a key for this
    /// session only. The file is left alone, so the next launch reads it again. A file that was read but
    /// does not hold a key: a new key is made and written there. The unreadable file is kept beside it
    /// as <c>peer.key.unreadable</c>.</para>
    ///
    /// <para>A read that fails must never write a new key over the old file. If it did, this machine
    /// would become a stranger to every peer.</para>
    /// </summary>
    internal static PeerIdentity LoadOrMake(string path)
    {
        if (!File.Exists(path))
            return Make(path);

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch
        {
            return PeerIdentity.Create();
        }

        try
        {
            return PeerIdentity.Import(bytes);
        }
        catch
        {
            try { File.Move(path, path + ".unreadable", overwrite: true); } catch { /* kept where it is */ }
            return Make(path);
        }
    }

    private static PeerIdentity Make(string path)
    {
        var identity = PeerIdentity.Create();
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, identity.Export());

            // This is the one file in the app's data folder that other users of the machine must not
            // read. The folder is already per user, so this is not a security boundary on its own. A
            // private key with world-readable permissions is a bug to fix, not something to explain.
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // If the data folder cannot be written, sharing works for this session only, and the key
            // is made again at the next launch. That is better than not starting.
        }

        return identity;
    }
}

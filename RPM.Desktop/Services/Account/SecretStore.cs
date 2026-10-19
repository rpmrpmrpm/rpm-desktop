namespace RPM.Desktop.Services.Account;

/// <summary>What reading a secret found.</summary>
public abstract record SecretRead
{
    private SecretRead() { }

    public sealed record Found(byte[] Bytes) : SecretRead;

    /// <summary>Nothing is stored under the name.</summary>
    public sealed record Absent : SecretRead;

    /// <summary>
    /// The store did not answer. This is not the same as <see cref="Absent"/>: something may still be
    /// stored, so a caller must not delete or replace anything because of this result.
    /// </summary>
    public sealed record Failed(string Detail) : SecretRead;
}

/// <summary>How a write or a delete ended. Deleting a name that holds nothing is <see cref="Done"/>.</summary>
public abstract record SecretWrite
{
    private SecretWrite() { }

    public sealed record Done : SecretWrite;

    public sealed record Failed(string Detail) : SecretWrite;
}

/// <summary>
/// A place to keep a small secret between launches, under a name.
///
/// <para>There are three kinds on disk. <see cref="SessionStore.For"/> picks one for each process. They
/// are the macOS data protection keychain (<see cref="MacKeychainStore"/>), a file sealed with DPAPI on
/// Windows (<see cref="DpapiFileStore"/>), and a file sealed with a key derived from this computer's
/// hardware id (<see cref="SealedFileStore"/>). The last one is the fallback when the others cannot
/// be used.</para>
///
/// <para>Every outcome is a value, not an exception. On a Debug build the keychain normally answers
/// that the build has no entitlement. If that threw, every launch under the debugger would raise a
/// first-chance exception.</para>
/// </summary>
public abstract class SecretStore
{
    /// <summary>The kind of store, for the log: "keychain", "dpapi" or "sealed file".</summary>
    public abstract string Name { get; }

    public abstract SecretRead Read(string name);

    /// <summary>Stores the secret under the name, replacing what was there.</summary>
    public abstract SecretWrite Write(string name, byte[] secret);

    public abstract SecretWrite Delete(string name);
}

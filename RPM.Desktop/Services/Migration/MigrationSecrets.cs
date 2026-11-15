using System.Text.Json;
using RPM.Desktop.Services.Account;

namespace RPM.Desktop.Services.Migration;

/// <summary>
/// The sign-ins kept for unfinished imports, one per source library, in the system keychain. Every write
/// and delete goes through here, under one lock. That lock, and <see cref="Replace"/> doing nothing when
/// no sign-in is kept, are what stop a late token renewal from bringing back a sign-in that a finished or
/// forgotten import deleted.
/// </summary>
internal sealed class MigrationSecrets(SecretStore store)
{
    private readonly Lock _lock = new();

    /// <summary>Keeps the connection under <paramref name="name"/>, replacing what was there.</summary>
    public SecretWrite Keep(string name, MigrationConnection connection)
    {
        lock (_lock)
            return store.Write(name, JsonSerializer.SerializeToUtf8Bytes(connection, MigrationServiceJson.Default.MigrationConnection));
    }

    /// <summary>Rewrites the connection kept under <paramref name="name"/>. Does nothing when none is kept,
    /// or when the store does not answer: a sign-in that was deleted stays deleted.</summary>
    public void Replace(string name, Func<MigrationConnection, MigrationConnection> change)
    {
        lock (_lock)
        {
            if (ReadLocked(name) is { } kept)
                store.Write(name, JsonSerializer.SerializeToUtf8Bytes(change(kept), MigrationServiceJson.Default.MigrationConnection));
        }
    }

    public void Delete(string name)
    {
        lock (_lock)
            store.Delete(name);
    }

    /// <summary>The connection kept under <paramref name="name"/>, or null when none is kept or it cannot be read.</summary>
    public MigrationConnection? Read(string name)
    {
        lock (_lock)
            return ReadLocked(name);
    }

    private MigrationConnection? ReadLocked(string name)
    {
        if (store.Read(name) is not SecretRead.Found { Bytes: var bytes })
            return null;
        try
        {
            return JsonSerializer.Deserialize(bytes, MigrationServiceJson.Default.MigrationConnection);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

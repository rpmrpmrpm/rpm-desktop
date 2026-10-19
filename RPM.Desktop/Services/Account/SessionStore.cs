using System.Text.Json;
using System.Text.Json.Serialization;
using RPM.Core.Diagnostics;
using RPM.Desktop.Services.Platform;

namespace RPM.Desktop.Services.Account;

/// <summary>What reading the stored session found.</summary>
public abstract record SessionRead
{
    private SessionRead() { }

    public sealed record Found(AuthService.Session Session) : SessionRead;

    public sealed record Absent : SessionRead;

    /// <summary>The store did not answer, or it holds something that is not a session. The app shows the
    /// person as signed out for this run. Nothing is deleted, so the next launch reads it again.</summary>
    public sealed record Failed(string Detail) : SessionRead;
}

/// <summary>
/// Where the signed-in session (the access token, the refresh token and who they belong to) is kept
/// between launches. <see cref="AuthService"/> takes one in its constructor, and it has no other way to
/// keep a session. So no code path writes the tokens anywhere else.
///
/// <para><b>Which store.</b> It is decided once per process by <see cref="For"/>, and only through it. A
/// harness run (<see cref="SessionHome.Harness"/>) gets a store in memory, and it reads nothing from disk.
/// Every other run gets the store that this build and this system can use. On a Mac build that has the
/// keychain entitlement, that is the data protection keychain. On Windows it is DPAPI. Otherwise it is the
/// sealed file. None of them ever asks the user for anything.</para>
///
/// <para><b>Older homes.</b> Earlier builds wrote the session as plain text to <c>auth.json</c>. A Mac build
/// without the entitlement writes the sealed file. When the chosen store holds no session, <see cref="Load"/>
/// looks in those older places. It copies what it finds into the chosen store, reads it back, and only then
/// deletes the older copy. A session that is already in the chosen store is never replaced by an older one.</para>
/// </summary>
public sealed partial class SessionStore
{
    /// <summary>The name the session is stored under, in every store.</summary>
    private const string SecretName = "session";

    /// <summary>The name of the list of refresh tokens that are waiting to be revoked. See
    /// <see cref="PendingRevocations"/>.</summary>
    private const string RevocationsName = "revocations";

    /// <summary>Held for each read-modify-write of the revocation list. A sign-out that adds a token, and a
    /// retry that removes the tokens it revoked, cannot overwrite each other this way.</summary>
    private readonly Lock _revocations = new();

    private readonly SecretStore _store;
    private readonly IReadOnlyList<SecretStore> _earlier;
    private readonly string? _plainTextPath;

    /// <param name="store">Where the session is written.</param>
    /// <param name="earlier">Stores that this process reads only to move a session out of them. Empty when
    /// <paramref name="store"/> is already the last fallback.</param>
    /// <param name="plainTextPath">The <c>auth.json</c> that earlier builds wrote, or null for a store with
    /// no older homes at all (a harness run's).</param>
    internal SessionStore(SecretStore store, IReadOnlyList<SecretStore> earlier, string? plainTextPath)
    {
        _store = store;
        _earlier = earlier;
        _plainTextPath = plainTextPath;
    }

    /// <summary>The store that a run reads and writes, when its sign-in is kept at <paramref name="home"/>.
    /// This is the only way to get one outside the tests. So the command line (<see cref="SessionHome.For"/>)
    /// is the one place that decides which sign-in a run may see.</summary>
    public static SessionStore For(SessionHome home) => home switch
    {
        // In memory, with no older homes and no auth.json. Nothing under LocalData is opened, not even the
        // folder's machine id, and nothing is probed.
        SessionHome.Harness => new SessionStore(new MemorySecretStore(), [], plainTextPath: null),
        SessionHome.ThisMachine => OnThisMachine(),
        _ => throw new ArgumentOutOfRangeException(nameof(home), home, "not a place a session is kept"),
    };

    /// <summary>The store that this machine and this build can use. The keychain is checked by adding and
    /// then deleting an item of its own. DPAPI is checked by sealing and opening two bytes in memory.</summary>
    private static SessionStore OnThisMachine()
    {
        var folder = Path.Combine(LocalData.Dir, "secrets");
        var plainText = Path.Combine(LocalData.Dir, "auth.json");
        var sealedFile = new SealedFileStore(folder, MachineId.Read(folder));

        if (OperatingSystem.IsMacOS())
        {
            if (MacKeychainStore.Works(out var status))
                return new SessionStore(new MacKeychainStore(), [sealedFile], plainText);

            // -34018 (errSecMissingEntitlement) is what every Debug build gets, and what a release build
            // gets when it is signed without the provisioning profile.
            Said.Line("auth", $"keychain not available to this build ({status}), session kept in the sealed file");
        }
        else if (OperatingSystem.IsWindows())
        {
            if (DpapiFileStore.Works(out var detail))
                return new SessionStore(new DpapiFileStore(folder), [sealedFile], plainText);

            Said.Line("auth", $"DPAPI not available ({detail}), session kept in the sealed file");
        }

        return new SessionStore(sealedFile, [], plainText);
    }

    /// <summary>The name of the store the session is written to, for the log.</summary>
    public string StoreName => _store.Name;

    /// <summary>Whether every place that this store reads is in memory. That means no store on disk, no
    /// older home and no <c>auth.json</c>. This is true for a harness run's store and for no other store.</summary>
    internal bool ReadsOnlyMemory => _store is MemorySecretStore && _earlier.Count == 0 && _plainTextPath is null;

    /// <summary>Reads the session. If the store holds none, it first moves the session out of an older home.</summary>
    public SessionRead Load()
    {
        switch (_store.Read(SecretName))
        {
            case SecretRead.Found found:
                if (Parse(found.Bytes) is not { } stored)
                {
                    Said.Line("auth", $"the {_store.Name} holds a session this build cannot read");
                    return new SessionRead.Failed("unreadable session");
                }

                // This is left over from a move that stopped between writing the store and deleting the file.
                DeletePlainText();
                return new SessionRead.Found(stored);

            case SecretRead.Failed failed:
                Said.Line("auth", $"session not read from the {_store.Name}: {failed.Detail}");
                return new SessionRead.Failed(failed.Detail);
        }

        foreach (var earlier in _earlier)
        {
            switch (earlier.Read(SecretName))
            {
                case SecretRead.Found found when Parse(found.Bytes) is { } session:
                    if (MoveIn(session, earlier.Name))
                        Report(earlier.Delete(SecretName), earlier.Name);
                    return new SessionRead.Found(session);

                case SecretRead.Found:
                    Said.Line("auth", $"the {earlier.Name} holds a session this build cannot read, left where it is");
                    break;

                case SecretRead.Failed failed:
                    Said.Line("auth", $"session not read from the {earlier.Name}: {failed.Detail}");
                    break;
            }
        }

        if (_plainTextPath is null || !File.Exists(_plainTextPath))
            return new SessionRead.Absent();

        AuthService.Session? plain;
        try
        {
            plain = Parse(File.ReadAllBytes(_plainTextPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Said.Line("auth", $"auth.json not read: {ex.GetType().Name}: {ex.Message}");
            return new SessionRead.Failed(ex.Message);
        }

        if (plain is null)
        {
            Said.Line("auth", "auth.json does not hold a session this build reads, left where it is");
            return new SessionRead.Failed("unreadable auth.json");
        }

        if (MoveIn(plain, "auth.json"))
            DeletePlainText();
        return new SessionRead.Found(plain);
    }

    /// <summary>Writes the session. A failure is written to the log, and the session still holds for this run.</summary>
    public void Save(AuthService.Session session)
    {
        if (_store.Write(SecretName, Serialize(session)) is SecretWrite.Failed failed)
        {
            Said.Line("auth", $"session not saved to the {_store.Name}: {failed.Detail}");
            return;
        }

        // A plain-text copy is always older than this one, so it can go.
        DeletePlainText();
    }

    /// <summary>Removes the session from every place that this process knows about: the store, the older
    /// stores and the plain-text file. After signing out, no session is left on disk.</summary>
    /// <returns>The refresh tokens of the sessions that were found in the older homes. They belong to other
    /// builds, or they were never moved. Deleting a copy does not end a token, so the caller revokes these
    /// itself.</returns>
    public IReadOnlyList<string> Forget()
    {
        var others = new List<string>();
        Report(_store.Delete(SecretName), _store.Name);
        foreach (var earlier in _earlier)
        {
            if (earlier.Read(SecretName) is SecretRead.Found found && Parse(found.Bytes) is { RefreshToken.Length: > 0 } session)
                others.Add(session.RefreshToken);
            Report(earlier.Delete(SecretName), earlier.Name);
        }

        try
        {
            if (_plainTextPath is not null && File.Exists(_plainTextPath)
                && Parse(File.ReadAllBytes(_plainTextPath)) is { RefreshToken.Length: > 0 } plain)
                others.Add(plain.RefreshToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Said.Line("auth", $"auth.json not read before deleting it: {ex.GetType().Name}");
        }
        DeletePlainText();
        return others;
    }

    /// <summary>
    /// Refresh tokens that a sign-out released, but that the server has not yet confirmed as revoked. They
    /// are kept in the same store as the session, so a waiting token is protected as well as a token in use.
    /// A token is written here before its revocation is sent. So if a sign-out happens with no network, or
    /// the app quits before the answer arrives, the token stays here for the next launch or sign-in to send
    /// again. This is empty when the store does not answer. That failure is written to the log.
    /// </summary>
    public IReadOnlyList<string> PendingRevocations()
    {
        lock (_revocations)
            return ReadRevocations(out _);
    }

    /// <summary>Adds tokens to <see cref="PendingRevocations"/>. Nothing is written when the list cannot
    /// be read, because a write would replace the tokens that are still in it.</summary>
    public void AddRevocations(IReadOnlyCollection<string> refreshTokens)
    {
        lock (_revocations)
        {
            var list = ReadRevocations(out var readable);
            if (!readable)
            {
                Said.Line("auth", $"{refreshTokens.Count} token(s) to revoke not written down, the {_store.Name} did not answer");
                return;
            }
            var added = refreshTokens.Where(t => t.Length > 0 && !list.Contains(t)).ToList();
            if (added.Count > 0)
                WriteRevocations([.. list, .. added]);
        }
    }

    /// <summary>Removes the tokens that the server has answered for from <see cref="PendingRevocations"/>.</summary>
    public void RemoveRevocations(IReadOnlyCollection<string> revoked)
    {
        lock (_revocations)
        {
            var list = ReadRevocations(out var readable);
            if (!readable)
                return;
            var left = list.Where(t => !revoked.Contains(t)).ToList();
            if (left.Count == list.Count)
                return;
            if (left.Count == 0)
                Report(_store.Delete(RevocationsName), _store.Name);
            else
                WriteRevocations(left);
        }
    }

    private List<string> ReadRevocations(out bool readable)
    {
        switch (_store.Read(RevocationsName))
        {
            case SecretRead.Found found:
                readable = true;
                try
                {
                    return JsonSerializer.Deserialize(found.Bytes, SessionJson.Default.StringArray)?.ToList() ?? [];
                }
                catch (JsonException)
                {
                    // Only this class writes the list. A list that does not parse holds nothing that can be sent.
                    Said.Line("auth", $"the list of tokens to revoke in the {_store.Name} does not parse, replaced");
                    return [];
                }

            case SecretRead.Failed failed:
                Said.Line("auth", $"tokens to revoke not read from the {_store.Name}: {failed.Detail}");
                readable = false;
                return [];

            default:
                readable = true;
                return [];
        }
    }

    private void WriteRevocations(IReadOnlyList<string> tokens)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(tokens.ToArray(), SessionJson.Default.StringArray);
        if (_store.Write(RevocationsName, bytes) is SecretWrite.Failed failed)
            Said.Line("auth", $"tokens to revoke not written to the {_store.Name}: {failed.Detail}");
    }

    /// <summary>Writes the session into the store, then reads it back. It returns true only when what comes
    /// back is what was written. That is the only case in which the older copy may be deleted.</summary>
    private bool MoveIn(AuthService.Session session, string from)
    {
        var bytes = Serialize(session);
        if (_store.Write(SecretName, bytes) is SecretWrite.Failed failed)
        {
            Said.Line("auth", $"session not moved from the {from} to the {_store.Name}: {failed.Detail}");
            return false;
        }

        if (_store.Read(SecretName) is not SecretRead.Found back || !back.Bytes.AsSpan().SequenceEqual(bytes))
        {
            Said.Line("auth", $"session written to the {_store.Name} did not read back, the {from} copy is kept");
            return false;
        }

        Said.Line("auth", $"session moved from the {from} to the {_store.Name}");
        return true;
    }

    private void DeletePlainText()
    {
        if (_plainTextPath is null || !File.Exists(_plainTextPath))
            return;
        try
        {
            File.Delete(_plainTextPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Said.Line("auth", $"auth.json not deleted: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Report(SecretWrite outcome, string store)
    {
        if (outcome is SecretWrite.Failed failed)
            Said.Line("auth", $"session not deleted from the {store}: {failed.Detail}");
    }

    private static byte[] Serialize(AuthService.Session session) =>
        JsonSerializer.SerializeToUtf8Bytes(session, SessionJson.Default.Session);

    /// <summary>The session, or null when the bytes are not one. The shape is the same one that
    /// <c>auth.json</c> always had, so the plain-text file is read under the same rules.</summary>
    private static AuthService.Session? Parse(byte[] bytes)
    {
        try
        {
            return JsonSerializer.Deserialize(bytes, SessionJson.Default.Session) is { AccessToken.Length: > 0 } session
                ? session
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [JsonSerializable(typeof(AuthService.Session))]
    [JsonSerializable(typeof(string[]))]
    private sealed partial class SessionJson : JsonSerializerContext;
}

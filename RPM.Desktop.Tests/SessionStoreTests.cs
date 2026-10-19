using System.Text;
using RPM.Desktop.Services.Account;

namespace RPM.Desktop.Tests;

/// <summary>
/// Where the sign-in tokens are kept between launches. What is checked: the tokens are not readable on
/// disk, the old plain-text <c>auth.json</c> is moved and then deleted, nothing is deleted on the
/// strength of a store that did not answer, and signing out leaves no copy anywhere.
/// </summary>
public class SessionStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "rpm-session-tests", Guid.NewGuid().ToString("n"));

    public SessionStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string PlainTextPath => Path.Combine(_dir, "auth.json");

    private static AuthService.Session Someone(string access = "access-token-123") => new(
        UserId: "u1",
        Email: "someone@example.com",
        DisplayName: "Someone",
        AccessToken: access,
        RefreshToken: "refresh-token-456",
        ExpiresUtc: new DateTime(2026, 10, 7, 21, 0, 0, DateTimeKind.Utc));

    private SealedFileStore Sealed(string folder, byte[]? machine = null) =>
        new(Path.Combine(_dir, folder), machine ?? TestSessions.Machine);

    /// <summary>The old file format, spelled out by hand rather than produced by the serializer. A change
    /// to the session's shape that breaks reading the old file fails here.</summary>
    private void WritePlainText(string access = "plain-access") => File.WriteAllText(PlainTextPath,
        "{\"UserId\":\"u1\",\"Email\":\"someone@example.com\",\"DisplayName\":\"Someone\"," +
        $"\"AccessToken\":\"{access}\",\"RefreshToken\":\"plain-refresh\",\"ExpiresUtc\":\"2026-10-07T21:00:00Z\"}}");

    [Fact]
    public void A_saved_session_is_there_at_the_next_launch()
    {
        TestSessions.InFolder(_dir).Save(Someone());

        var read = TestSessions.InFolder(_dir).Load();

        Assert.Equal(Someone(), Assert.IsType<SessionRead.Found>(read).Session);
    }

    [Fact]
    public void The_tokens_are_not_readable_on_disk()
    {
        TestSessions.InFolder(_dir).Save(Someone());

        var everything = Directory.GetFiles(_dir, "*", SearchOption.AllDirectories)
            .Select(f => Encoding.UTF8.GetString(File.ReadAllBytes(f)))
            .ToList();

        Assert.NotEmpty(everything);
        Assert.DoesNotContain(everything, text => text.Contains("access-token-123"));
        Assert.DoesNotContain(everything, text => text.Contains("refresh-token-456"));
        Assert.DoesNotContain(everything, text => text.Contains("someone@example.com"));
    }

    [Fact]
    public void The_sealed_files_are_the_owners_alone()
    {
        if (OperatingSystem.IsWindows())
            return;

        TestSessions.InFolder(_dir).Save(Someone());
        var folder = Path.Combine(_dir, "secrets");

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(folder));
        foreach (var file in Directory.GetFiles(folder))
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
    }

    [Fact]
    public void The_plain_text_file_of_earlier_builds_is_moved_and_then_deleted()
    {
        WritePlainText();

        var read = TestSessions.InFolder(_dir).Load();

        Assert.Equal("plain-access", Assert.IsType<SessionRead.Found>(read).Session.AccessToken);
        Assert.False(File.Exists(PlainTextPath));

        // And the next launch finds it in the store.
        var again = TestSessions.InFolder(_dir).Load();
        Assert.Equal("plain-refresh", Assert.IsType<SessionRead.Found>(again).Session.RefreshToken);
    }

    [Fact]
    public void A_plain_text_session_the_store_cannot_take_is_used_and_kept()
    {
        WritePlainText();
        var store = new SessionStore(new Unwritable(), [], PlainTextPath);

        var read = store.Load();

        Assert.IsType<SessionRead.Found>(read);
        Assert.True(File.Exists(PlainTextPath));
    }

    [Fact]
    public void A_store_that_does_not_answer_deletes_nothing()
    {
        WritePlainText();
        var earlier = Sealed("earlier");
        Assert.IsType<SecretWrite.Done>(earlier.Write("session", Encoding.UTF8.GetBytes("{}")));

        var read = new SessionStore(new Unanswering(), [earlier], PlainTextPath).Load();

        Assert.IsType<SessionRead.Failed>(read);
        Assert.True(File.Exists(PlainTextPath));
        Assert.IsType<SecretRead.Found>(earlier.Read("session"));
    }

    [Fact]
    public void An_unreadable_plain_text_file_is_not_a_session_and_is_kept()
    {
        File.WriteAllText(PlainTextPath, "not json");

        var read = TestSessions.InFolder(_dir).Load();

        Assert.IsType<SessionRead.Failed>(read);
        Assert.True(File.Exists(PlainTextPath));
    }

    [Fact]
    public void A_session_in_an_earlier_store_moves_into_the_store()
    {
        var store = Sealed("store");
        var earlier = Sealed("earlier");
        new SessionStore(earlier, [], PlainTextPath).Save(Someone("from-earlier"));

        var read = new SessionStore(store, [earlier], PlainTextPath).Load();

        Assert.Equal("from-earlier", Assert.IsType<SessionRead.Found>(read).Session.AccessToken);
        Assert.IsType<SecretRead.Found>(store.Read("session"));
        Assert.IsType<SecretRead.Absent>(earlier.Read("session"));
    }

    /// <summary>A Debug build keeps its sign-in in the sealed file and a signed release in the
    /// keychain. The release must not take the Debug build's session over its own.</summary>
    [Fact]
    public void A_session_already_in_the_store_wins_and_the_earlier_one_is_left_alone()
    {
        var store = Sealed("store");
        var earlier = Sealed("earlier");
        new SessionStore(store, [], PlainTextPath).Save(Someone("current"));
        new SessionStore(earlier, [], PlainTextPath).Save(Someone("other-build"));
        WritePlainText();

        var read = new SessionStore(store, [earlier], PlainTextPath).Load();

        Assert.Equal("current", Assert.IsType<SessionRead.Found>(read).Session.AccessToken);
        Assert.IsType<SecretRead.Found>(earlier.Read("session"));
        Assert.False(File.Exists(PlainTextPath));
    }

    [Fact]
    public void Saving_deletes_a_plain_text_copy_left_behind()
    {
        WritePlainText();

        new SessionStore(Sealed("store"), [], PlainTextPath).Save(Someone());

        Assert.False(File.Exists(PlainTextPath));
    }

    [Fact]
    public void Forgetting_removes_the_session_from_every_place()
    {
        var store = Sealed("store");
        var earlier = Sealed("earlier");
        new SessionStore(store, [], PlainTextPath).Save(Someone());
        new SessionStore(earlier, [], PlainTextPath).Save(Someone());
        WritePlainText();

        new SessionStore(store, [earlier], PlainTextPath).Forget();

        Assert.IsType<SecretRead.Absent>(store.Read("session"));
        Assert.IsType<SecretRead.Absent>(earlier.Read("session"));
        Assert.False(File.Exists(PlainTextPath));
    }

    /// <summary>A copy of the folder on another computer does not open, and says so rather than
    /// reading as "nothing stored".</summary>
    [Fact]
    public void Another_computer_cannot_open_the_sealed_file()
    {
        Assert.IsType<SecretWrite.Done>(Sealed("store").Write("session", [1, 2, 3]));

        var elsewhere = Sealed("store", machine: new byte[32]);

        Assert.IsType<SecretRead.Failed>(elsewhere.Read("session"));
    }

    [Fact]
    public void A_sealed_file_renamed_to_another_name_does_not_open()
    {
        var store = Sealed("store");
        Assert.IsType<SecretWrite.Done>(store.Write("one", [1, 2, 3]));
        var folder = Path.Combine(_dir, "store");
        File.Copy(Path.Combine(folder, "one.sealed"), Path.Combine(folder, "two.sealed"));

        Assert.IsType<SecretRead.Failed>(store.Read("two"));
    }

    [Fact]
    public void A_name_with_a_folder_in_it_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Sealed("store").Read("../session"));
    }

    /// <summary>
    /// The test host has no keychain entitlement, like every Debug build. The keychain must say so with
    /// errSecMissingEntitlement, through a return value: no exception, and no dialog. This is the answer
    /// that sends a build without the provisioning profile to the sealed file. (A read cannot tell:
    /// asked for an item that is not there, an unentitled build is told it is not there.)
    /// </summary>
    [Fact]
    public void Without_the_entitlement_the_keychain_says_so_and_asks_nothing()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        Assert.False(MacKeychainStore.Works(out var status));
        Assert.Equal(-34018, status);
    }

    [Fact]
    public void Dpapi_seals_for_this_user_and_this_name()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.True(DpapiFileStore.Works(out var detail), detail);
        var store = new DpapiFileStore(Path.Combine(_dir, "dpapi"));
        var secret = Encoding.UTF8.GetBytes("access-token-123");

        Assert.IsType<SecretWrite.Done>(store.Write("session", secret));
        Assert.Equal(secret, Assert.IsType<SecretRead.Found>(store.Read("session")).Bytes);

        var onDisk = File.ReadAllBytes(Path.Combine(_dir, "dpapi", "session.dpapi"));
        Assert.DoesNotContain("access-token-123", Encoding.UTF8.GetString(onDisk));

        File.Copy(Path.Combine(_dir, "dpapi", "session.dpapi"), Path.Combine(_dir, "dpapi", "other.dpapi"));
        Assert.IsType<SecretRead.Failed>(store.Read("other"));

        Assert.IsType<SecretWrite.Done>(store.Delete("session"));
        Assert.IsType<SecretRead.Absent>(store.Read("session"));
    }

    /// <summary>Holds nothing, and refuses every write.</summary>
    private sealed class Unwritable : SecretStore
    {
        public override string Name => "unwritable";
        public override SecretRead Read(string name) => new SecretRead.Absent();
        public override SecretWrite Write(string name, byte[] secret) => new SecretWrite.Failed("refused");
        public override SecretWrite Delete(string name) => new SecretWrite.Done();
    }

    /// <summary>Answers no read, as a keychain might that has stopped responding.</summary>
    private sealed class Unanswering : SecretStore
    {
        public override string Name => "unanswering";
        public override SecretRead Read(string name) => new SecretRead.Failed("no answer");
        public override SecretWrite Write(string name, byte[] secret) => new SecretWrite.Failed("no answer");
        public override SecretWrite Delete(string name) => new SecretWrite.Failed("no answer");
    }
}

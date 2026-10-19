using System.Collections.Concurrent;
using System.Text;
using RPM.Desktop.Services.Account;

namespace RPM.Desktop.Tests;

/// <summary>
/// Signing out revokes the refresh token on the server. So a copy of it kept elsewhere, such as in a backup
/// of the plain-text <c>auth.json</c>, stops working. The token is written down before it is sent, and
/// taken off the list only once the server has answered. So a sign-out with no network, or a quit before
/// the answer, is sent again by the next launch.
/// </summary>
public class RevocationTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "rpm-revoke-tests", Guid.NewGuid().ToString("n"));

    public RevocationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static AuthService.Session SignedIn(string refresh = "refresh-1") => new(
        UserId: "u1",
        Email: "someone@example.com",
        DisplayName: "Someone",
        AccessToken: "access",
        RefreshToken: refresh,
        ExpiresUtc: DateTime.UtcNow.AddHours(1));

    /// <summary>The revocation runs in the background, so the test waits for the state it is about
    /// rather than for a moment.</summary>
    private static async Task Eventually(Func<bool> condition, string what)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(25);
        Assert.True(condition(), what);
    }

    private IReadOnlyList<string> Pending() => TestSessions.InFolder(_dir).PendingRevocations();

    [Fact]
    public async Task Signing_out_revokes_the_refresh_token_and_leaves_nothing_to_send()
    {
        var sent = new ConcurrentQueue<string>();
        var auth = new AuthService(TestSessions.InFolder(_dir))
        {
            RevokeForTest = token => { sent.Enqueue(token); return Task.FromResult(true); },
        };
        auth.SeedSession(SignedIn());

        auth.Logout();

        await Eventually(() => sent.Contains("refresh-1") && Pending().Count == 0,
            "the refresh token was revoked and taken off the list");
        Assert.False(auth.IsLoggedIn);
    }

    [Fact]
    public async Task A_revocation_with_no_answer_is_sent_again_at_the_next_launch()
    {
        var tried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var offline = new AuthService(TestSessions.InFolder(_dir))
        {
            RevokeForTest = _ => { tried.TrySetResult(); return Task.FromResult(false); },
        };
        offline.SeedSession(SignedIn());

        offline.Logout();
        await tried.Task;
        await Task.Delay(100);
        Assert.Equal(["refresh-1"], Pending());

        var sent = new ConcurrentQueue<string>();
        var nextLaunch = new AuthService(TestSessions.InFolder(_dir))
        {
            RevokeForTest = token => { sent.Enqueue(token); return Task.FromResult(true); },
        };
        nextLaunch.Load();

        // Still signed out: the token waiting to be revoked is not a session.
        Assert.False(nextLaunch.IsLoggedIn);
        await Eventually(() => sent.Contains("refresh-1") && Pending().Count == 0,
            "the next launch sent the revocation and took it off the list");
    }

    /// <summary>A keychain that does not answer cannot hold the token for later, but the revocation is
    /// still sent now.</summary>
    [Fact]
    public async Task A_store_that_does_not_answer_still_sends_the_revocation()
    {
        var sent = new ConcurrentQueue<string>();
        var auth = new AuthService(new SessionStore(new Unanswering(), [], Path.Combine(_dir, "auth.json")))
        {
            RevokeForTest = token => { sent.Enqueue(token); return Task.FromResult(true); },
        };
        auth.SeedSession(SignedIn());

        auth.Logout();

        await Eventually(() => sent.Contains("refresh-1"), "the revocation was sent");
    }

    /// <summary>A retry removes the tokens it sent, not the list it read, so a sign-out that adds a
    /// token while the retry is in the air keeps it.</summary>
    [Fact]
    public void A_token_added_while_a_retry_runs_is_kept()
    {
        var store = TestSessions.InFolder(_dir);
        store.AddRevocations(["a"]);
        var theRetrySends = store.PendingRevocations();

        store.AddRevocations(["b"]);
        store.RemoveRevocations(theRetrySends.ToList());

        Assert.Equal(["b"], store.PendingRevocations());
    }

    /// <summary>Sessions in older storage locations belong to other builds, or were never moved. Deleting
    /// them ends this computer's copy and not the token, so sign-out revokes them too.</summary>
    [Fact]
    public async Task Signing_out_also_revokes_the_sessions_it_deletes_from_older_homes()
    {
        var earlier = new SealedFileStore(Path.Combine(_dir, "earlier"), TestSessions.Machine);
        var plainText = Path.Combine(_dir, "auth.json");
        new SessionStore(earlier, [], Path.Combine(_dir, "unused.json")).Save(SignedIn("from-a-debug-build"));
        File.WriteAllText(plainText,
            "{\"UserId\":\"u1\",\"Email\":\"someone@example.com\",\"DisplayName\":null,\"AccessToken\":\"a\"," +
            "\"RefreshToken\":\"from-auth-json\",\"ExpiresUtc\":\"2026-10-07T21:00:00Z\"}");

        var sent = new ConcurrentQueue<string>();
        var store = new SessionStore(new SealedFileStore(Path.Combine(_dir, "store"), TestSessions.Machine), [earlier], plainText);
        var auth = new AuthService(store)
        {
            RevokeForTest = token => { sent.Enqueue(token); return Task.FromResult(true); },
        };
        auth.SeedSession(SignedIn("this-one"));

        auth.Logout();

        await Eventually(() => sent.Contains("this-one") && sent.Contains("from-a-debug-build") && sent.Contains("from-auth-json"),
            "all three tokens were revoked");
        Assert.IsType<SecretRead.Absent>(earlier.Read("session"));
        Assert.False(File.Exists(plainText));
    }

    /// <summary>Answers no read and takes no write, as a keychain might that has stopped
    /// responding.</summary>
    private sealed class Unanswering : SecretStore
    {
        public override string Name => "unanswering";
        public override SecretRead Read(string name) => new SecretRead.Failed("no answer");
        public override SecretWrite Write(string name, byte[] secret) => new SecretWrite.Failed("no answer");
        public override SecretWrite Delete(string name) => new SecretWrite.Failed("no answer");
    }
}

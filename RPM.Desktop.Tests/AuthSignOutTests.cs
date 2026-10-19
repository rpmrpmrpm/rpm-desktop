using RPM.Desktop.Services.Account;

namespace RPM.Desktop.Tests;

/// <summary>
/// Signing out must not ask for a fresh token.
///
/// <para>
/// The loop this prevents: an expired session makes <c>GetAccessTokenAsync</c> refresh; a refresh
/// that fails calls <c>Logout</c>; <c>Logout</c> raises <c>SigningOut</c>; a <c>SigningOut</c>
/// handler makes an HTTP call, which asks for a token, which is still expired. Nothing in that loop
/// counts down and nothing stops it.
/// </para>
///
/// <para>
/// It overflows the stack instead of only spinning, because the network gate refuses a request
/// without using the network when the machine is offline. Every await then completes synchronously,
/// so each turn nests instead of looping (about a thousand turns on one stack, seconds after a
/// laptop wakes up).
/// </para>
/// </summary>
public class AuthSignOutTests : IDisposable
{
    // Never the real store. Logout forgets the session, and the real store holds the session of
    // whoever is signed in on this machine, so a test run would sign the developer out of their own
    // copy of the app.
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "rpm-auth-tests", Guid.NewGuid().ToString("n"));

    /// <summary>An AuthService that keeps its session in this test's own folder.</summary>
    private AuthService NewAuth()
    {
        Directory.CreateDirectory(_dir);
        var auth = TestSessions.Auth(_dir);
        auth.SeedSession(Stale());
        return auth;
    }

    /// <summary>What a later launch would find in this test's store.</summary>
    private SessionRead Stored() => TestSessions.InFolder(_dir).Load();

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>An expired session, which is what makes GetAccessTokenAsync want to refresh.</summary>
    private static AuthService.Session Stale() => new(
        UserId: "u1",
        Email: "someone@example.com",
        DisplayName: "Someone",
        AccessToken: "stale-access-token",
        RefreshToken: "refresh-token",
        ExpiresUtc: DateTime.UtcNow.AddHours(-1));

    [Fact]
    public void ASignOutHandlerAskingForATokenDoesNotStartAnotherSignOut()
    {
        var auth = NewAuth();

        var entered = 0;
        string? handed = null;

        auth.SigningOut += async () =>
        {
            // The cap is the test's own guard. If the loop is not prevented, this handler is entered
            // again and again until the stack runs out, which would crash the test runner too.
            if (++entered > 4) return;

            handed = await auth.GetAccessTokenAsync();
        };

        auth.Logout();

        Assert.Equal(1, entered);
        Assert.Equal("stale-access-token", handed);
    }

    [Fact]
    public void TheTokenIsHandedOverDuringSignOutRatherThanRefreshed()
    {
        var auth = NewAuth();

        string? handed = null;
        var sawTheFlag = false;

        auth.SigningOut += async () =>
        {
            // The purpose of the event: the handlers tell the account this computer is leaving, and
            // they need the current token to send that message.
            sawTheFlag = auth.IsSigningOut;
            handed = await auth.GetAccessTokenAsync();
        };

        auth.Logout();

        Assert.True(sawTheFlag);
        Assert.Equal("stale-access-token", handed);
    }

    [Fact]
    public void TheSessionIsGoneOnceSignOutHasFinished()
    {
        var auth = NewAuth();

        auth.Logout();

        Assert.False(auth.IsLoggedIn);
        Assert.False(auth.IsSigningOut);
    }

    [Fact]
    public async Task ABurstOfCallersOnAnExpiredSessionMakesOneRefresh()
    {
        var auth = NewAuth();

        var refreshes = 0;
        auth.RefreshForTest = async _ =>
        {
            Interlocked.Increment(ref refreshes);
            // Long enough that every caller in the burst is inside GetAccessTokenAsync while this
            // one runs. Without the lock they would all be starting their own.
            await Task.Delay(50);
            return new AuthService.RefreshResponse("fresh-access-token", 3600);
        };

        var handed = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(_ => Task.Run(() => auth.GetAccessTokenAsync())));

        // The server rotates refresh tokens: a second refresh uses one the first has replaced, is
        // refused, and a refused refresh signs the user out for no reason.
        Assert.Equal(1, refreshes);
        Assert.All(handed, t => Assert.Equal("fresh-access-token", t));
        Assert.True(auth.IsLoggedIn);
    }

    [Fact]
    public async Task ARefusedRefreshSignsOutOnceRatherThanOncePerCaller()
    {
        var auth = NewAuth();

        var signOuts = 0;
        auth.SigningOut += () => { Interlocked.Increment(ref signOuts); return Task.CompletedTask; };

        // No token returned: the server's answer when the refresh token is already used.
        auth.RefreshForTest = async _ => { await Task.Delay(20); return null; };

        var handed = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(_ => Task.Run(() => auth.GetAccessTokenAsync())));

        Assert.Equal(1, signOuts);
        Assert.All(handed, Assert.Null);
        Assert.False(auth.IsLoggedIn);
    }

    [Fact]
    public async Task ANetworkFailureHandsBackTheStaleTokenAndDoesNotSignOut()
    {
        var auth = NewAuth();

        var signOuts = 0;
        auth.SigningOut += () => { Interlocked.Increment(ref signOuts); return Task.CompletedTask; };

        // A throw is the network; a null answer is the account. Only the second means the session
        // is really finished.
        auth.RefreshForTest = _ => throw new HttpRequestException("no route to host");

        Assert.Equal("stale-access-token", await auth.GetAccessTokenAsync());
        Assert.Equal(0, signOuts);
        Assert.True(auth.IsLoggedIn);
    }

    [Theory]
    [InlineData(400, true)]
    [InlineData(401, true)]
    [InlineData(403, true)]
    [InlineData(404, false)]
    [InlineData(429, false)]
    [InlineData(500, false)]
    [InlineData(502, false)]
    [InlineData(503, false)]
    [InlineData(504, false)]
    public void OnlyARefusalStatusSignsOut(int status, bool refusal)
    {
        // A cold Lambda (502/504), a throttle (429) or a short Cognito outage (503) says nothing
        // about the refresh token. Signing out on those would sign users out every day or two.
        Assert.Equal(refusal, AuthService.IsRefusal((System.Net.HttpStatusCode)status));
    }

    [Fact]
    public async Task AServerErrorDuringRefreshKeepsTheSession()
    {
        var auth = NewAuth();

        var signOuts = 0;
        auth.SigningOut += () => { Interlocked.Increment(ref signOuts); return Task.CompletedTask; };

        // What FetchRefreshAsync throws for a status that is not a refusal.
        auth.RefreshForTest = _ => throw new HttpRequestException(
            "auth/refresh answered 503", null, System.Net.HttpStatusCode.ServiceUnavailable);

        Assert.Equal("stale-access-token", await auth.GetAccessTokenAsync());
        Assert.Equal(0, signOuts);
        Assert.True(auth.IsLoggedIn);
    }

    [Fact]
    public async Task ASignOutHandlerAskingForATokenDoesNotWaitOnTheRefreshThatCalledIt()
    {
        var auth = NewAuth();

        string? handed = null;
        auth.SigningOut += async () => { handed = await auth.GetAccessTokenAsync(); };
        auth.RefreshForTest = _ => Task.FromResult<AuthService.RefreshResponse?>(null);

        // The refusal calls Logout from inside the refresh, which still holds the lock. A handler
        // that reached the lock would wait on the refresh that is calling it, so this deadlocks if
        // the signing-out check ever moves below the wait.
        var done = Task.Run(() => auth.GetAccessTokenAsync());

        // Raced against a timer, not blocked on: a test that detects a deadlock by blocking a thread
        // adds one more thread that can deadlock.
        var finished = await Task.WhenAny(done, Task.Delay(TimeSpan.FromSeconds(10))) == done;
        Assert.True(finished, "signing out deadlocked against its own refresh");
        Assert.Equal("stale-access-token", handed);
    }

    [Fact]
    public async Task ARefreshThatLandsAfterASignOutDoesNotPutTheUserBackIn()
    {
        var auth = NewAuth();

        var inTheAir = new TaskCompletionSource();
        var letItLand = new TaskCompletionSource();

        auth.RefreshForTest = async _ =>
        {
            inTheAir.SetResult();
            await letItLand.Task;
            return new AuthService.RefreshResponse("fresh-access-token", 3600);
        };

        var refreshing = Task.Run(() => auth.GetAccessTokenAsync());
        await inTheAir.Task;

        // The user signs out from the UI while the refresh is still in flight. Logout does not wait
        // for it: waiting would freeze the interface for the length of an HTTP call.
        auth.Logout();
        Assert.False(auth.IsLoggedIn);

        letItLand.SetResult();

        Assert.Null(await refreshing);
        Assert.False(auth.IsLoggedIn);
        Assert.IsType<SessionRead.Absent>(Stored());
    }

    /// <summary>
    /// The narrower case: the refresh has already passed its check on the sign-out count and is
    /// writing its session when the sign-out arrives. The write and the count must share a lock.
    /// Otherwise the sign-out runs completely in that gap and forgets the stored session, and the
    /// write then puts it back: signed out on screen, signed in again at the next launch.
    /// </summary>
    [Fact]
    public async Task ASignOutDuringARefreshsWriteLeavesNothingStored()
    {
        Directory.CreateDirectory(_dir);
        var store = new HeldWrites();
        var auth = TestSessions.Offline(new AuthService(new SessionStore(store, [], Path.Combine(_dir, "auth.json"))));
        auth.SeedSession(Stale());
        auth.RefreshForTest = _ => Task.FromResult<AuthService.RefreshResponse?>(
            new AuthService.RefreshResponse("fresh-access-token", 3600));

        var refreshing = Task.Run(() => auth.GetAccessTokenAsync());
        await store.Writing.Task;

        var signingOut = Task.Run(auth.Logout);

        // Time for the sign-out to run ahead of the write, which is the ordering under test. With the
        // lock it waits at the count instead, and the delay changes nothing.
        await Task.Delay(200);
        store.Release.Set();

        var both = Task.WhenAll(refreshing, signingOut);
        Assert.True(await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(10))) == both,
            "the sign-out and the refresh's write deadlocked");
        Assert.False(auth.IsLoggedIn);
        Assert.IsType<SecretRead.Absent>(store.Read("session"));
    }

    [Fact]
    public async Task ARefreshThatFailsAfterASignOutDoesNotSpeakForTheOldSession()
    {
        var auth = NewAuth();

        var inTheAir = new TaskCompletionSource();
        var letItLand = new TaskCompletionSource();

        auth.RefreshForTest = async _ =>
        {
            inTheAir.SetResult();
            await letItLand.Task;
            throw new HttpRequestException("no route to host");
        };

        var refreshing = Task.Run(() => auth.GetAccessTokenAsync());
        await inTheAir.Task;

        auth.Logout();
        letItLand.SetResult();

        // The stale-token answer is for a session that still exists. After a sign-out there is none.
        Assert.Null(await refreshing);
        Assert.False(auth.IsLoggedIn);
    }

    [Fact]
    public async Task RefreshingIsOnAgainAfterASignOut()
    {
        var auth = NewAuth();

        auth.Logout();

        // The flag is temporary, not permanent. With no session there is nothing to return, which
        // is the answer a signed-out account gives.
        Assert.False(auth.IsSigningOut);
        Assert.Null(await auth.GetAccessTokenAsync());
    }

    /// <summary>Keeps secrets in memory, and holds each write until the test lets it finish. Stands
    /// in for a slow keychain write.</summary>
    private sealed class HeldWrites : SecretStore
    {
        private readonly Dictionary<string, byte[]> _items = new();

        public TaskCompletionSource Writing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim Release { get; } = new(false);

        public override string Name => "held";

        public override SecretRead Read(string name)
        {
            lock (_items)
                return _items.TryGetValue(name, out var bytes) ? new SecretRead.Found(bytes) : new SecretRead.Absent();
        }

        public override SecretWrite Write(string name, byte[] secret)
        {
            Writing.TrySetResult();
            Release.Wait(TimeSpan.FromSeconds(10));
            lock (_items)
                _items[name] = secret;
            return new SecretWrite.Done();
        }

        public override SecretWrite Delete(string name)
        {
            lock (_items)
                _items.Remove(name);
            return new SecretWrite.Done();
        }
    }
}

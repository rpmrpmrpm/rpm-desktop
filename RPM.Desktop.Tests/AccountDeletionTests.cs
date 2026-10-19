using RPM.Desktop.Services.Account;

namespace RPM.Desktop.Tests;

/// <summary>
/// What the accounts service's answer to a deletion means in the app. Only a 2xx deletes anything
/// here; a refusal and a failure both leave the user signed in with nothing changed.
/// </summary>
public class AccountDeletionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rpm-delete-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(204, AccountDeletionOutcome.Deleted)]
    [InlineData(200, AccountDeletionOutcome.Deleted)]
    [InlineData(400, AccountDeletionOutcome.EmailRefused)]
    [InlineData(401, AccountDeletionOutcome.Failed)]
    [InlineData(500, AccountDeletionOutcome.Failed)]
    [InlineData(503, AccountDeletionOutcome.Failed)]
    public void The_servers_answer_decides_what_happens_here(int status, AccountDeletionOutcome expected)
    {
        Assert.Equal(expected, AuthService.DeletionAnswer(status));
    }

    /// <summary>Nobody signed in, nothing to delete, and nothing sent.</summary>
    [Fact]
    public async Task Signed_out_there_is_nothing_to_delete()
    {
        Directory.CreateDirectory(_dir);
        var auth = TestSessions.Auth(_dir);

        var outcome = await auth.DeleteAccountOnServerAsync("someone@example.com", default);

        Assert.Equal(AccountDeletionOutcome.Failed, outcome);
        Assert.False(auth.IsLoggedIn);
    }
}

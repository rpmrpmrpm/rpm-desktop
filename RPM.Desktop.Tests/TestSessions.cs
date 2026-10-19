using System.Security.Cryptography;
using System.Text;
using RPM.Desktop.Services.Account;

namespace RPM.Desktop.Tests;

/// <summary>
/// Session stores for tests, in folders of the tests' own. Never the keychain, DPAPI or the app's own
/// folder: a test that signs out against the real store signs the developer out of their copy of the
/// app.
/// </summary>
internal static class TestSessions
{
    /// <summary>Stands for this computer's id, so the sealed file opens the same on every machine the
    /// tests run on.</summary>
    public static readonly byte[] Machine = SHA256.HashData(Encoding.UTF8.GetBytes("rpm-tests-machine"));

    /// <summary>A sealed-file store in <paramref name="folder"/>, with a plain-text <c>auth.json</c> beside
    /// it, as an old build would have left it. Nothing is created until something is written.</summary>
    public static SessionStore InFolder(string folder) =>
        new(new SealedFileStore(Path.Combine(folder, "secrets"), Machine), [], Path.Combine(folder, "auth.json"));

    /// <summary>An <see cref="AuthService"/> keeping its session in <paramref name="folder"/>, whose
    /// sign-out revocations are answered here rather than sent to the accounts service.</summary>
    public static AuthService Auth(string folder) => Offline(new AuthService(InFolder(folder)));

    /// <summary>Answers every revocation as done, so a test that signs out sends nothing to the real
    /// accounts service. A test that is about revoking sets <c>RevokeForTest</c> itself.</summary>
    public static AuthService Offline(AuthService auth)
    {
        auth.RevokeForTest = _ => Task.FromResult(true);
        return auth;
    }

    /// <summary>A signed-out <see cref="AuthService"/> for a test that never signs in. Its folder is
    /// only created if something is written to it.</summary>
    public static AuthService SignedOut() =>
        Auth(Path.Combine(Path.GetTempPath(), "rpm-auth-tests", Guid.NewGuid().ToString("n")));
}

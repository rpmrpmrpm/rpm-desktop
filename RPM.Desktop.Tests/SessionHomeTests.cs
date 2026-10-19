using RPM.Desktop.Services.Account;

namespace RPM.Desktop.Tests;

/// <summary>
/// A design-harness run must not read the sign-in kept on this computer. A Debug <c>--gallery</c> run on
/// Windows could otherwise read the installed app's DPAPI session. The list push and the cloud upload
/// would then act on the demo library as the owner. What is checked: every <c>--gallery</c> flag makes a
/// harness run, an ordinary launch does not, and a harness run's store is built only in memory. So it
/// starts signed out and keeps nothing for the next run.
/// </summary>
public class SessionHomeTests
{
    [Fact]
    public void An_ordinary_launch_reads_the_sign_in_kept_on_this_computer()
    {
        string[][] launches =
        [
            ["rpm.exe"],
            ["rpm.exe", "--library", "D:/music"],
            ["rpm.exe", "--rpamp"],
            ["rpm.exe", "--place-genres"],
            ["rpm.exe", "--rip-disc"],
        ];

        foreach (var commandLine in launches)
            Assert.IsType<SessionHome.ThisMachine>(SessionHome.For(commandLine));
    }

#if DEBUG
    private static SessionHome.Harness GalleryRun() =>
        Assert.IsType<SessionHome.Harness>(SessionHome.For(["rpm.exe", "--gallery"]));

    [Theory]
    [InlineData("--gallery")]
    [InlineData("--gallery-wall")]
    [InlineData("--gallery-shelf=C:/covers")]
    public void Every_gallery_flag_makes_a_harness_run(string flag)
    {
        var home = SessionHome.For(["rpm.exe", flag]);

        Assert.Equal(flag, Assert.IsType<SessionHome.Harness>(home).Flag);
    }

    [Fact]
    public void A_harness_run_flag_anywhere_on_the_command_line_counts()
    {
        var home = SessionHome.For(["rpm.exe", "--shoot=bars", "--gallery"]);

        Assert.IsType<SessionHome.Harness>(home);
    }

    [Fact]
    public void A_harness_runs_store_is_built_on_memory_alone()
    {
        var store = SessionStore.For(GalleryRun());

        // No disk store, no old folder to move a session in from, and no auth.json to read: there is
        // no path in it that leads to the app's local data folder.
        Assert.True(store.ReadsOnlyMemory);
        Assert.Equal("memory store", store.StoreName);
    }

    [Fact]
    public void A_harness_run_starts_signed_out()
    {
        var auth = TestSessions.Offline(new AuthService(SessionStore.For(GalleryRun())));

        auth.Load();

        Assert.False(auth.IsLoggedIn);
        Assert.Null(auth.UserId);
    }

    [Fact]
    public void A_sign_in_during_a_harness_run_is_not_kept_for_the_next()
    {
        var session = new AuthService.Session(
            UserId: "u1", Email: "someone@example.com", DisplayName: "Someone",
            AccessToken: "access", RefreshToken: "refresh",
            ExpiresUtc: new DateTime(2026, 10, 13, 21, 0, 0, DateTimeKind.Utc));
        SessionStore.For(GalleryRun()).Save(session);

        var nextRun = SessionStore.For(GalleryRun());

        Assert.IsType<SessionRead.Absent>(nextRun.Load());
    }
#endif
}

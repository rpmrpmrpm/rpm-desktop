using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using RPM.Core.Diagnostics;
using RPM.Core.Ripping;
using RPM.Desktop.Services.Account;
using RPM.Desktop.Services.Cloud;
using RPM.Desktop.Services.Library;
using RPM.Desktop.Services.Sharing;

namespace RPM.Desktop.Tests;

/// <summary>
/// What the desktop says in place of an exception's message. The message must not be shown on screen,
/// because an exception message is written for developers. For example, signing in offline would show "No
/// such host is known".
///
/// <para>These types hand out string keys rather than calling <c>Loc</c>, so the repo-wide guard over
/// <c>strings.json</c> cannot see them. Each key is checked here instead: present in all five
/// languages, with no dash, and with exactly the placeholders its arguments fill.</para>
/// </summary>
public class TroubleTextTests
{
    private static readonly string[] Locales = ["en", "fr", "es", "de", "zh-Hans"];

    // ── sign-in ──

    [Fact]
    public void A_wrong_password_and_an_unconfirmed_address_are_told_apart_by_the_status()
    {
        Assert.Equal(SignInRefusal.WrongPassword, SignInOutcome.FromLoginStatus(HttpStatusCode.Unauthorized).Why);
        Assert.Equal(SignInRefusal.NotConfirmed, SignInOutcome.FromLoginStatus(HttpStatusCode.BadRequest).Why);
        Assert.Equal(SignInRefusal.Other, SignInOutcome.FromLoginStatus(HttpStatusCode.InternalServerError).Why);
    }

    [Fact]
    public void Signing_in_with_no_network_says_so_and_not_what_dotnet_said()
    {
        var offline = new SignInOutcome.Failed(
            Faults.Read(new HttpRequestException("No such host is known.", new SocketException(11001))));

        Assert.Equal("Fault_Offline", offline.StringKey);
    }

    [Fact]
    public void A_server_error_while_signing_in_is_could_not_sign_in_and_not_signed_out()
    {
        Assert.Equal("Login_Unable", new SignInOutcome.Failed(Fault.SignedOut).StringKey);
        Assert.Equal("Login_Unable", new SignInOutcome.Failed(Fault.Server).StringKey);
        Assert.Equal("Login_Unable", new SignInOutcome.Failed(Fault.Unknown).StringKey);
        Assert.Equal("Fault_Timeout", new SignInOutcome.Failed(Fault.Timeout).StringKey);
    }

    [Fact]
    public void Success_and_a_cancelled_sign_in_say_nothing()
    {
        Assert.Null(new SignInOutcome.SignedIn().StringKey);
        Assert.Null(new SignInOutcome.Abandoned().StringKey);
        Assert.Null(new SignInOutcome.Failed(Fault.None).StringKey);
    }

    [Fact]
    public void Every_sign_in_sentence_is_in_the_table()
    {
        SignInOutcome[] all =
        [
            new SignInOutcome.Refused(SignInRefusal.WrongPassword),
            new SignInOutcome.Refused(SignInRefusal.NotConfirmed),
            new SignInOutcome.Refused(SignInRefusal.BrowserTimedOut),
            new SignInOutcome.Refused(SignInRefusal.Other),
            new SignInOutcome.Failed(Fault.Offline),
            new SignInOutcome.Failed(Fault.Unknown),
        ];

        AssertInTable(all.Select(o => (o.StringKey!, 0)));
    }

    // ── sign-up ──

    [Theory]
    [InlineData("already_registered", "SignUp_AlreadyRegistered")]
    [InlineData("social_account", "SignUp_SocialAccount")]
    [InlineData("weak_password", "SignUp_WeakPassword")]
    [InlineData("invalid_email", "SignUp_InvalidEmail")]
    [InlineData("invalid_name", "SignUp_InvalidName")]
    [InlineData("invalid_code", "SignUp_InvalidCode")]
    [InlineData("code_resent", "SignUp_CodeResent")]
    [InlineData("too_many", "SignUp_TooMany")]
    [InlineData("something_new", "SignUp_Failed")]
    public void Each_sign_up_refusal_has_its_own_sentence(string code, string key) =>
        Assert.Equal(key, new SignUpAnswer.Refused(code).StringKey);

    [Fact]
    public void The_refusal_code_is_read_from_the_body_and_not_the_sentence()
    {
        Assert.Equal("too_many", SignUpAnswer.FromRefusal("""{"error":"Too many attempts.","code":"too_many"}""").Code);
        Assert.Equal("refused", SignUpAnswer.FromRefusal("""{"error":"name, email and password are required."}""").Code);
        Assert.Equal("refused", SignUpAnswer.FromRefusal("<html>502 Bad Gateway</html>").Code);
        Assert.Equal("refused", SignUpAnswer.FromRefusal("").Code);
    }

    [Fact]
    public void Signing_up_with_no_network_says_so_and_not_could_not_make_the_account()
    {
        var offline = new SignUpAnswer.Failed(
            Faults.Read(new HttpRequestException("No such host is known.", new SocketException(11001))));

        Assert.Equal("Fault_Offline", offline.StringKey);
        Assert.Equal("Fault_Timeout", new SignUpAnswer.Failed(Fault.Timeout).StringKey);
        Assert.Equal("SignUp_Failed", new SignUpAnswer.Failed(Fault.Server).StringKey);
    }

    [Fact]
    public void A_finished_step_says_nothing() => Assert.Null(new SignUpAnswer.Done().StringKey);

    [Fact]
    public void Every_sign_up_sentence_is_in_the_table()
    {
        SignUpAnswer[] all =
        [
            .. new[] { "already_registered", "social_account", "weak_password", "invalid_email", "invalid_name",
                       "invalid_code", "code_resent", "too_many", "refused" }
                .Select(code => (SignUpAnswer)new SignUpAnswer.Refused(code)),
            new SignUpAnswer.Failed(Fault.Offline),
            new SignUpAnswer.Failed(Fault.Unknown),
        ];

        AssertInTable(all.Select(a => (a.StringKey!, 0)));
    }

    // ── cloud ──

    [Fact]
    public void A_cloud_failure_carries_the_reading_and_not_the_message()
    {
        var stopped = new CloudTrouble.SyncStopped(
            Faults.Read(new HttpRequestException("No such host is known.", new SocketException(11001))));

        Assert.Equal("Cloud_SyncStopped", stopped.StringKey);
        Assert.Equal([Fault.Offline], stopped.Args);
    }

    [Fact]
    public void Storage_full_is_a_case_and_not_a_sentence_compared_by_value()
    {
        CloudTrouble? last = new CloudTrouble.StorageFull();

        Assert.IsType<CloudTrouble.StorageFull>(last);
        Assert.Equal(new CloudTrouble.StorageFull(), last);
    }

    [Fact]
    public void Every_cloud_sentence_is_in_the_table_with_a_place_for_each_argument()
    {
        CloudTrouble[] all =
        [
            new CloudTrouble.StorageFull(),
            new CloudTrouble.ManyTracksMissing(),
            new CloudTrouble.BringBackFailed(2, 10),
            new CloudTrouble.SyncStopped(Fault.Offline),
            new CloudTrouble.TracksNotRemoved(2, 10, Fault.Server),
            new CloudTrouble.PlaylistNotRemoved(Fault.Timeout),
            new CloudTrouble.PlaylistNotStored("Sunday", Fault.Unknown),
        ];

        AssertInTable(all.Select(t => (t.StringKey, t.Args.Length)));
    }

    // ── sharing ──

    [Fact]
    public void Sharing_on_the_network_does_not_blame_the_internet()
    {
        // A Fault would read a dropped LAN connection as "No internet connection.", which is wrong for
        // a computer in the same room. Only the cloud case carries one.
        Assert.Empty(new SharingTrouble.Serving().Args);
        Assert.DoesNotContain(new SharingTrouble.Reading("Study").Args, a => a is Fault);
        Assert.Contains(new SharingTrouble.Cloud(Fault.Offline).Args, a => a is Fault);
    }

    [Fact]
    public void Every_sharing_sentence_is_in_the_table_with_a_place_for_each_argument()
    {
        SharingTrouble[] all =
        [
            new SharingTrouble.Serving(),
            new SharingTrouble.Reading("Study"),
            new SharingTrouble.Cloud(Fault.Offline),
        ];

        AssertInTable(all.Select(t => (t.StringKey, t.Args.Length)));
    }

    // ── ripping ──

    [Fact]
    public void A_failed_rip_says_what_happened_to_the_disc()
    {
        Assert.Equal("AddMusic_RipUnreadable", RipJobService.FailureKey(new DiscReadException("toc")));
        Assert.Equal("AddMusic_RipUnreadable", RipJobService.FailureKey(new DiscTrackUnreadableException("t3")));
        Assert.Equal(
            OperatingSystem.IsWindows() ? "AddMusic_DvdNeedsLibDvdCssWindows" : "AddMusic_DvdNeedsLibDvdCss",
            RipJobService.FailureKey(new DvdEncryptedException("css")));
    }

    [Fact]
    public void A_rip_that_filled_the_disk_says_the_disk_is_full()
    {
        Assert.Equal("Fault_NoSpace", RipJobService.FailureKey(new IOException("No space left on device")));
        Assert.Equal("Fault_Unknown", RipJobService.FailureKey(new InvalidOperationException("ffmpeg")));
    }

    [Fact]
    public void Every_rip_sentence_is_in_the_table()
    {
        Exception[] all =
        [
            new DiscReadException("x"), new DvdEncryptedException("x"),
            new IOException("No space left on device"), new InvalidOperationException("x"),
        ];

        AssertInTable(all.Select(ex => (RipJobService.FailureKey(ex), 0)));
    }

    // ── the table ──

    /// <summary>Each key is in strings.json in every language, with no dash, and with placeholders
    /// {0} to {n-1} and no others, where n is how many arguments the case fills.</summary>
    private static void AssertInTable(IEnumerable<(string Key, int Args)> keys)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(SharedFiles.Path("strings.json")));

        var problems = new List<string>();
        foreach (var (key, args) in keys.Distinct())
        {
            if (!doc.RootElement.TryGetProperty(key, out var entry))
            {
                problems.Add($"{key} is not in strings.json");
                continue;
            }

            foreach (var locale in Locales)
            {
                var text = entry.TryGetProperty(locale, out var t) ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(text))
                {
                    problems.Add($"{key}: no {locale}");
                    continue;
                }

                if (text.IndexOfAny(['—', '–']) >= 0)
                    problems.Add($"{key}: {locale} has a dash");

                var used = Regex.Matches(text, @"\{(\d+)\}").Select(m => int.Parse(m.Groups[1].Value)).ToHashSet();
                if (!used.SetEquals(Enumerable.Range(0, args)))
                    problems.Add($"{key}: {locale} uses {{{string.Join(",", used.Order())}}}, the case fills {args}");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>Walks up from the test binaries to the folder holding RPM.sln.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RPM.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}

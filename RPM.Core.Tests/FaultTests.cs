using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using RPM.Core.Diagnostics;
using RPM.Fingerprint.Chroma;

namespace RPM.Core.Tests;

/// <summary>
/// How a failure is told to the user (<see cref="Fault"/>) and how it is kept for the log and a report
/// (<see cref="Faults.InFull(Exception)"/>). <c>ex.Message</c> must not be shown on screen: signing
/// in offline would show "No such host is known".
/// </summary>
public class FaultTests
{
    [Fact]
    public void A_cancellation_is_not_a_failure_and_says_nothing()
    {
        Assert.Equal(Fault.None, Faults.Read(new OperationCanceledException()));
        Assert.Equal(Fault.None, Faults.Read(new TaskCanceledException()));
        Assert.Equal(Fault.None, Faults.Read(null));
        Assert.Null(Fault.None.StringKey());
    }

    [Fact]
    public void A_timed_out_request_is_a_timeout_and_not_a_cancellation()
    {
        // HttpClient's own timeout: a TaskCanceledException with the TimeoutException inside.
        var timedOut = new TaskCanceledException("timed out", new TimeoutException());

        Assert.Equal(Fault.Timeout, Faults.Read(timedOut));
        Assert.Equal(Fault.Timeout, Faults.Read(new TimeoutException()));
    }

    [Fact]
    public void Signing_in_with_no_network_reads_as_offline()
    {
        // What HttpClient throws for "No such host is known", a line the sign-in dialog must not show.
        var dns = new HttpRequestException("No such host is known.", new SocketException(11001));

        Assert.Equal(Fault.Offline, Faults.Read(dns));
        Assert.Equal(Fault.Offline, Faults.Read(new SocketException((int)SocketError.HostNotFound)));
        Assert.Equal(Fault.Offline, Faults.Read(new HttpRequestException("refused", new IOException("reset"))));
    }

    [Fact]
    public void A_refused_token_is_something_to_sign_in_for()
    {
        Assert.Equal(Fault.SignedOut,
            Faults.Read(new HttpRequestException("no", null, HttpStatusCode.Unauthorized)));
        Assert.Equal(Fault.SignedOut,
            Faults.Read(new HttpRequestException("no", null, HttpStatusCode.Forbidden)));
    }

    [Fact]
    public void Any_other_answer_from_the_server_is_the_server_saying_no()
    {
        Assert.Equal(Fault.Server,
            Faults.Read(new HttpRequestException("no", null, HttpStatusCode.InternalServerError)));
        Assert.Equal(Fault.Server,
            Faults.Read(new HttpRequestException("no", null, HttpStatusCode.NotFound)));
    }

    [Fact]
    public void Files_and_folders_read_as_what_happened_to_them()
    {
        Assert.Equal(Fault.NoPermission, Faults.Read(new UnauthorizedAccessException()));
        Assert.Equal(Fault.FileMissing, Faults.Read(new FileNotFoundException()));
        Assert.Equal(Fault.FileMissing, Faults.Read(new DirectoryNotFoundException()));
    }

    [Fact]
    public void A_full_disk_is_told_apart_from_a_file_in_use()
    {
        // Windows reports both with a bare IOException; the HResult's low word is the Win32 error.
        var full = new IOException("disk", unchecked((int)0x80070070));     // ERROR_DISK_FULL
        var inUse = new IOException("locked", unchecked((int)0x80070020));  // ERROR_SHARING_VIOLATION

        Assert.Equal(Fault.NoSpace, Faults.Read(full));
        Assert.Equal(Fault.FileInUse, Faults.Read(inUse));

        // macOS reports them in words.
        Assert.Equal(Fault.NoSpace, Faults.Read(new IOException("No space left on device : '/x'")));
        Assert.Equal(Fault.FileInUse,
            Faults.Read(new IOException("The process cannot access the file because it is being used by another process.")));
    }

    [Fact]
    public void A_plain_io_failure_is_not_guessed_at()
    {
        Assert.Equal(Fault.Unknown, Faults.Read(new IOException("something else")));
    }

    [Fact]
    public void Bytes_that_cannot_be_made_sense_of_are_unreadable()
    {
        Assert.Equal(Fault.Unreadable, Faults.Read(new InvalidDataException()));
        Assert.Equal(Fault.Unreadable, Faults.Read(new JsonException()));
        Assert.Equal(Fault.Unreadable, Faults.Read(new FormatException()));
    }

    [Fact]
    public void A_file_fpcalc_could_not_read_is_unreadable_and_a_broken_install_is_not()
    {
        Assert.Equal(Fault.Unreadable, Faults.Read(new FpcalcException(FpcalcFailure.Unreadable, "x")));
        Assert.Equal(Fault.Unreadable, Faults.Read(new FpcalcException(FpcalcFailure.Unopened, "x")));
        Assert.Equal(Fault.Unknown, Faults.Read(new FpcalcException(FpcalcFailure.Unavailable, "x")));
        Assert.Equal(Fault.Unknown, Faults.Read(new FpcalcException(FpcalcFailure.Unknown, "x")));
    }

    [Fact]
    public void Looks_inside_a_wrapper()
    {
        var dns = new HttpRequestException("No such host is known.", new SocketException(11001));

        Assert.Equal(Fault.Offline, Faults.Read(new AggregateException(dns)));
        Assert.Equal(Fault.Offline, Faults.Read(new TargetInvocationException(dns)));
    }

    [Fact]
    public void Anything_else_is_unknown_and_still_has_a_sentence()
    {
        var fault = Faults.Read(new InvalidOperationException("Sequence contains no elements"));

        Assert.Equal(Fault.Unknown, fault);
        Assert.Equal("Fault_Unknown", fault.StringKey());
    }

    [Fact]
    public void The_full_text_keeps_what_the_screen_does_not_show()
    {
        Exception ex;
        try { throw new HttpRequestException("No such host is known.", new SocketException(11001)); }
        catch (Exception caught) { ex = caught; }

        var full = Faults.InFull(ex, home: "");

        // The .NET wording is gone from the screen, not from the report.
        Assert.StartsWith("HttpRequestException: No such host is known.", full);
        Assert.Contains("SocketException:", full);
        Assert.Contains(nameof(The_full_text_keeps_what_the_screen_does_not_show), full);
    }

    [Fact]
    public void The_full_text_writes_the_home_folder_as_a_tilde()
    {
        var ex = new FileNotFoundException("Could not find file '/Users/someone/Music/a.flac'.");

        var full = Faults.InFull(ex, home: "/Users/someone");

        Assert.Contains("'~/Music/a.flac'", full);
        Assert.DoesNotContain("someone", full);
    }

    [Fact]
    public void Noting_a_cancellation_writes_nothing_and_says_nothing()
    {
        Assert.Equal(Fault.None, Faults.Note("test", "a test", new OperationCanceledException()));
        Assert.Equal(Fault.None, Faults.Note("test", "a test", null));
    }

    [Fact]
    public void Noting_a_failure_reads_it_the_same_way()
    {
        var dns = new HttpRequestException("No such host is known.", new SocketException(11001));

        Assert.Equal(Faults.Read(dns), Faults.Note("test", "a test", dns));
    }

    /// <summary>The keys are reached through <see cref="Faults.StringKey"/>, a variable, so
    /// LocalizationKeyTests cannot see them. A missing one renders as its own name on screen.</summary>
    [Fact]
    public void Every_fault_worth_saying_has_a_sentence_in_every_language()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(StringsJson()));
        var table = doc.RootElement;

        var problems = new List<string>();
        foreach (var fault in Enum.GetValues<Fault>())
        {
            if (fault.StringKey() is not { } key)
                continue;

            if (!table.TryGetProperty(key, out var entry))
            {
                problems.Add($"{fault}: {key} is not in strings.json");
                continue;
            }

            foreach (var locale in new[] { "en", "fr", "es", "de", "zh-Hans" })
            {
                if (!entry.TryGetProperty(locale, out var text) || string.IsNullOrWhiteSpace(text.GetString()))
                    problems.Add($"{key}: no {locale}");
                else if (text.GetString()!.IndexOfAny(['—', '–']) >= 0)
                    problems.Add($"{key}: {locale} has a dash");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    private static string StringsJson() => SharedFiles.Path("strings.json");
}

using RPM.Fingerprint.Chroma;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using RPM.Fingerprint;

namespace RPM.Core.Diagnostics;

/// <summary>
/// What a failed step is told to the user as.
///
/// <para>A fixed set, so that no .NET exception message can reach a screen. Raw exception messages
/// are not shown: they are English only, and they often tell the user nothing they can act on. For
/// example, a sign-in with no network would show "No such host is known". The exception itself is
/// not thrown away. It goes to the ring and into a report (<see cref="Faults.Note"/>,
/// <see cref="Faults.InFull(Exception)"/>).</para>
///
/// <para>The head turns one of these into the user's own language with
/// <see cref="Faults.StringKey"/>. <c>RPM.Core</c> has no string table of its own.</para>
/// </summary>
public enum Fault
{
    /// <summary>Nothing to say: the step was abandoned on purpose. Never shown.</summary>
    None,

    /// <summary>No network, or the name would not resolve.</summary>
    Offline,

    /// <summary>The network is there and the answer never came.</summary>
    Timeout,

    /// <summary>The session is not valid any more. Signing in again is the fix.</summary>
    SignedOut,

    /// <summary>The server answered, and what it said was no.</summary>
    Server,

    /// <summary>The operating system refused the file or the folder.</summary>
    NoPermission,

    /// <summary>The disk the file was being written to is full.</summary>
    NoSpace,

    /// <summary>The file or folder is not there any more.</summary>
    FileMissing,

    /// <summary>Another program is holding the file open.</summary>
    FileInUse,

    /// <summary>The bytes arrived and could not be made sense of.</summary>
    Unreadable,

    /// <summary>Anything else.</summary>
    Unknown,
}

public static class Faults
{
    /// <summary>
    /// Reads an exception as one of the <see cref="Fault"/>s, and writes everything known about it to
    /// the ring. Use this wherever the answer reaches a screen: it is the one call that cannot be made
    /// without the detail being kept.
    /// </summary>
    /// <param name="channel">The ring channel the detail is written under: "auth", "cloud", "cast".</param>
    /// <param name="what">What was being done, in a few words: "sign-in", "sync pass". The first thing
    /// on the line, so a reader of the ring can tell one failure from the next.</param>
    /// <param name="error">The failure. Null reads as <see cref="Fault.None"/> and writes nothing.</param>
    public static Fault Note(string channel, string what, Exception? error)
    {
        if (error is null)
            return Fault.None;

        var fault = Read(error);

        // A cancellation is not a failure and a line per skipped track is noise.
        if (fault != Fault.None)
            Said.Line(channel, $"{what} failed ({fault}): {InFull(error)}");

        return fault;
    }

    /// <summary>
    /// Reads an exception as one of the <see cref="Fault"/>s, and writes nothing. For a caller that
    /// keeps the detail itself, and for tests. <see cref="Note"/> is what a screen's path uses.
    ///
    /// <para>Cancellation is <see cref="Fault.None"/> and a timeout is not, which is the distinction
    /// the framework makes hardest: <see cref="HttpClient"/> reports its own timeout as a
    /// <see cref="TaskCanceledException"/>, the same type it throws when the caller cancels, and the
    /// only thing telling them apart is a <see cref="TimeoutException"/> inside it.</para>
    /// </summary>
    public static Fault Read(Exception? error) => error switch
    {
        null => Fault.None,

        // This must come before the plain cancellation case below. Otherwise every timed-out
        // request reads as a cancellation.
        TaskCanceledException { InnerException: TimeoutException } => Fault.Timeout,
        TimeoutException => Fault.Timeout,
        OperationCanceledException => Fault.None,

        HttpRequestException http => FromHttp(http),

        // Thrown before a request is made when there is no route at all.
        SocketException => Fault.Offline,

        UnauthorizedAccessException => Fault.NoPermission,
        FileNotFoundException or DirectoryNotFoundException => Fault.FileMissing,

        // Order matters: both of these are IOException, and so is the generic case under them.
        IOException io when IsDiskFull(io) => Fault.NoSpace,
        IOException io when IsInUse(io) => Fault.FileInUse,

        InvalidDataException or JsonException or FormatException => Fault.Unreadable,

        // fpcalc has already read its own stderr. A file it could not open or could not fingerprint
        // is a file that could not be read; a broken install or an unknown failure is not that.
        FpcalcException { Failure: FpcalcFailure.Unreadable or FpcalcFailure.Unopened } => Fault.Unreadable,

        // The failure is somewhere inside: unwrap and read that instead.
        AggregateException { InnerException: { } inner } => Read(inner),
        TargetInvocationException { InnerException: { } inner } => Read(inner),

        _ => Fault.Unknown,
    };

    /// <summary>The resource key the head shows for this fault, or null for the one that is not worth
    /// saying. One mapping rather than a switch on every screen.</summary>
    public static string? StringKey(this Fault fault) => fault switch
    {
        Fault.None => null,
        Fault.Offline => "Fault_Offline",
        Fault.Timeout => "Fault_Timeout",
        Fault.SignedOut => "Fault_SignedOut",
        Fault.Server => "Fault_Server",
        Fault.NoPermission => "Fault_NoPermission",
        Fault.NoSpace => "Fault_NoSpace",
        Fault.FileMissing => "Fault_FileMissing",
        Fault.FileInUse => "Fault_FileInUse",
        Fault.Unreadable => "Fault_Unreadable",
        _ => "Fault_Unknown",
    };

    private static Fault FromHttp(HttpRequestException http)
    {
        if (http.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return Fault.SignedOut;

        if (http.StatusCode is not null)
            return Fault.Server;

        // No status means the request never got an answer: DNS, no route, a refused connection.
        return http.InnerException is SocketException or IOException || http.InnerException is null
            ? Fault.Offline
            : Fault.Unknown;
    }

    /// <summary>ERROR_DISK_FULL and ERROR_HANDLE_DISK_FULL as Windows reports them, and the wording
    /// both platforms use. There is no exception type for a full disk on either.</summary>
    private static bool IsDiskFull(IOException io) =>
        (io.HResult & 0xFFFF) is 0x27 or 0x70
        || Mentions(io, "No space left on device")
        || Mentions(io, "There is not enough space on the disk");

    /// <summary>ERROR_SHARING_VIOLATION and ERROR_LOCK_VIOLATION, and the wording .NET uses.</summary>
    private static bool IsInUse(IOException io) =>
        (io.HResult & 0xFFFF) is 0x20 or 0x21
        || Mentions(io, "being used by another process")
        || Mentions(io, "Resource temporarily unavailable");

    private static bool Mentions(Exception ex, string phrase) =>
        ex.Message.Contains(phrase, StringComparison.OrdinalIgnoreCase);

    /// <summary>The longest text <see cref="InFull(Exception)"/> returns. The server keeps 8,000
    /// characters of a report's detail (ErrorReportLimits.MaxDetail in RPM.Lambda.Api).</summary>
    public const int MaxFullError = 8_000;

    /// <summary>
    /// Everything known about a failure, for the ring and for a report the user chooses to send.
    ///
    /// <para>Every exception's type and message down the chain, then ffmpeg's complete output when
    /// ffmpeg was what failed (<see cref="FfmpegFailedException.Output"/>; the message carries only
    /// the short reading of it), then the stack. The user's home folder is written as <c>~</c>, the
    /// way crash reports do it, because a tool repeats the full path of the file it was given.</para>
    ///
    /// <para>Never shown in the app's own text. A screen shows the sentence for the
    /// <see cref="Fault"/>; this is for whoever reads the report.</para>
    /// </summary>
    public static string InFull(Exception ex) =>
        InFull(ex, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <param name="ex">The failure.</param>
    /// <param name="home">The folder written as <c>~</c>. Empty to leave paths alone.</param>
    public static string InFull(Exception ex, string home)
    {
        ArgumentNullException.ThrowIfNull(ex);

        var text = new StringBuilder();
        for (Exception? e = ex; e is not null; e = e.InnerException)
            text.Append(e.GetType().Name).Append(": ").AppendLine(e.Message);

        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is FfmpegFailedException { Output.Length: > 0 } ffmpeg)
                text.AppendLine().AppendLine("ffmpeg:").AppendLine(ffmpeg.Output.Trim());

        if (ex.StackTrace is { Length: > 0 } stack)
            text.AppendLine().Append(stack);

        var full = WithoutHome(text.ToString().TrimEnd(), home);
        return full.Length > MaxFullError ? full[..MaxFullError] : full;
    }

    /// <summary>The home folder as <c>~</c>, in either slash direction and either case: a tool echoes
    /// a path as it was given, and Windows paths reach it in both.</summary>
    private static string WithoutHome(string text, string home)
    {
        if (string.IsNullOrEmpty(home))
            return text;

        var trimmed = home.TrimEnd('\\', '/');
        return text
            .Replace(trimmed, "~", StringComparison.OrdinalIgnoreCase)
            .Replace(trimmed.Replace('\\', '/'), "~", StringComparison.OrdinalIgnoreCase);
    }
}

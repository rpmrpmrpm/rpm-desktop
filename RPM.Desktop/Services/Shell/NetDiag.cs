using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Text;

namespace RPM.Desktop.Services.Shell;

/// <summary>
/// Every request this app puts on the wire, tallied by host. Also every one that did not come back,
/// tallied by what went wrong.
///
/// <para>It answers one question that reading the code cannot answer. Scrolling sets off
/// network traffic, and the caches in the path should prevent that. The genre wall asks for a picture
/// once per genre and keeps it. The artist's chosen photograph is settled once per session. A borrowed
/// cover is written to disk when it first arrives. Each of these looks correct when it is read, but the
/// sockets still churn. So the fetch must happen somewhere else. The only reliable way to find it is to
/// count what actually leaves the machine.</para>
///
/// <para><b>Listened for at the runtime, not wired into a client.</b> The app has several HttpClient
/// instances: for pictures, the catalogues, the cloud and the peers. Instrumenting them one at a time
/// would only find the ones already suspected. The <c>System.Net.Http</c> event source reports every
/// request that any of them makes. So a host that appears in the tally is the answer, whether or not
/// anyone suspected it.</para>
///
/// <para><b>Connections as well as requests.</b> The socket errors in the output window are not failed
/// requests. A failed request is reported once, while these errors arrive in groups of four. They come
/// from connections being torn down. Only this class can show that, by comparing
/// <c>ConnectionEstablished</c> with <c>RequestStart</c>. Thirty requests over three connections means
/// the pool is working. Thirty requests over thirty connections means the pool is not being reused. Each
/// of those closes raises an IOException from the TLS stream as it goes down.</para>
///
/// <para>Requests are counted, never logged one by one. One line per request is what made the output
/// unreadable. What is wanted is a count such as "sixty requests to one host in the second you
/// scrolled".</para>
/// </summary>
internal sealed partial class NetDiag : EventListener
{
    /// <summary>On for its own sake, and on for free whenever either of the diagnostics it answers
    /// questions alongside is on. The wall's stall line prints the tally; the exception trace names the
    /// throw sites, and this says which host they were on.</summary>
    public static bool On =>
        RPM.Core.Diagnostics.DevSwitch.Read("RPM_NET_DIAG") is "1" or "true" ||
        Presentation.Main.Library.WallDiag.On ||
        ExceptionTrace.On;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, int> ByHost = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The same count, by what was asked for. This half says why. A host name shows that the
    /// artwork service is busy. A count of "/artwork 62" shows that it is being asked sixty-two questions,
    /// which points to a caller with a loop in it.</summary>
    private static readonly Dictionary<string, int> ByPath = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Sockets opened, by host. Read against <see cref="ByHost"/>, the ratio shows whether the
    /// connection pool is being reused or churned. Churn is what fills the output window.</summary>
    private static readonly Dictionary<string, int> Opened = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Sockets closed. This is not kept by host, because the runtime's <c>ConnectionClosed</c>
    /// event carries only the version and the connection's id. The host was reported once, when the
    /// connection was opened. A close is where the IOException errors come from. So compare this count
    /// with the noise in the output window. If the two move together, the noise is caused by these
    /// closes.</summary>
    private static int _closed;

    /// <summary>Requests that came back with nothing, by the message the stack gave for it. This is the
    /// list that answers "what web requests are failing and why", and it is kept for the whole run
    /// rather than emptied per report: a failure that happened twice an hour ago still happened.</summary>
    private static readonly Dictionary<string, int> Failures = new(StringComparer.Ordinal);

    /// <summary>Everything since the run started, so the closing report is about the run and not about
    /// the last second of it.</summary>
    private static readonly Dictionary<string, int> RunByHost = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> RunByPath = new(StringComparer.OrdinalIgnoreCase);

    private static int _total;
    private static int _runTotal;
    private static int _runOpened;
    private static int _runClosed;
    private static NetDiag? _listening;
    private static Timer? _beat;

    /// <summary>How often the tally is written out on its own. The wall diagnostic prints it on its
    /// stall lines, but a stall line only happens when the wall stalls, and the question here is what
    /// leaves while nothing at all is happening.</summary>
    private static readonly TimeSpan Beat = TimeSpan.FromSeconds(10);

    /// <summary>Starts counting.</summary>
    public static void Install()
    {
        if (_listening is not null || !On)
            return;

        _listening = new NetDiag();
        _beat = new Timer(_ => Report("net"), null, Beat, Beat);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Final();
        Say("[net-diag] on: requests, connections and failures counted by host");
    }

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "System.Net.Http")
            EnableEvents(source, EventLevel.Informational);
    }

    protected override void OnEventWritten(EventWrittenEventArgs written)
    {
        // The shapes below are the runtime's to change, so every read is guarded and a payload that is
        // not what this expects is dropped rather than trusted: a diagnostic that throws inside the
        // network stack would be worse than no diagnostic at all.
        switch (written.EventName)
        {
            // RequestStart(scheme, host, port, pathAndQuery, …)
            case "RequestStart" when written.Payload is { Count: >= 2 }:
                Started(written);
                break;

            // ConnectionEstablished(versionMajor, versionMinor, connectionId, scheme, host, port, …)
            case "ConnectionEstablished" when written.Payload is { Count: >= 5 }:
                Bump(Opened, written.Payload[4] as string, ref _runOpened);
                break;

            // ConnectionClosed(versionMajor, versionMinor, connectionId). It has no host, so this one is
            // only ever a number. It is guarded the same way anyway, because the shape belongs to the
            // runtime.
            case "ConnectionClosed" when written.Payload is { Count: >= 3 }:
                lock (Gate)
                {
                    _closed++;
                    _runClosed++;
                }
                break;

            // RequestFailed() carries nothing; RequestFailedDetailed(exception) carries the stack. Both
            // are listened for, because which of them the runtime raises has moved between versions and
            // a diagnostic that silently stops reporting is the worst kind.
            case "RequestFailed":
            case "RequestFailedDetailed":
                Failed(written);
                break;
        }
    }

    private static void Started(EventWrittenEventArgs written)
    {
        if (written.Payload![1] as string is not { Length: > 0 } host)
            return;

        // Without its query. The query makes every one of a hundred identical calls look different.
        // /artwork?artist=A and /artwork?artist=B are one caller asking twice.
        var asked = written.Payload.Count >= 4 ? written.Payload[3] as string : null;
        var path = Route(asked);

        lock (Gate)
        {
            _total++;
            _runTotal++;
            ByHost[host] = ByHost.GetValueOrDefault(host) + 1;
            RunByHost[host] = RunByHost.GetValueOrDefault(host) + 1;
            if (path is not null)
            {
                ByPath[path] = ByPath.GetValueOrDefault(path) + 1;
                RunByPath[path] = RunByPath.GetValueOrDefault(path) + 1;
            }
        }
    }

    private static void Bump(Dictionary<string, int> into, string? host, ref int total)
    {
        if (host is not { Length: > 0 })
            return;

        lock (Gate)
        {
            into[host] = into.GetValueOrDefault(host) + 1;
            total++;
        }
    }

    /// <summary>A request that came back with nothing, filed under the first line of its message. The
    /// rest of an exception's text is the stack, and the stack repeats. The first line is the reason,
    /// which is what a list of failures should show.</summary>
    private static void Failed(EventWrittenEventArgs written)
    {
        var said = written.Payload?.FirstOrDefault() as string;
        var reason = Reason(said) ?? "(no message)";

        lock (Gate)
            Failures[reason] = Failures.GetValueOrDefault(reason) + 1;
    }

    private static string? Reason(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var line = text.AsSpan();
        var cut = line.IndexOfAny('\r', '\n');
        if (cut >= 0)
            line = line[..cut];

        var one = line.Trim().ToString();
        return one.Length switch
        {
            0 => null,
            > 160 => one[..160] + "…",
            _ => one,
        };
    }

    /// <summary>What was asked for, with the query cut off and the length capped. Null when there is
    /// nothing worth printing. A bare "/" says nothing more than the host name does.</summary>
    private static string? Route(string? pathAndQuery)
    {
        if (string.IsNullOrEmpty(pathAndQuery))
            return null;

        var cut = pathAndQuery.IndexOf('?');
        var path = cut >= 0 ? pathAndQuery[..cut] : pathAndQuery;
        return path.Length switch
        {
            <= 1 => null,
            > 48 => path[..48] + "…",
            _ => path,
        };
    }

    /// <summary>What went out since this was last asked, and then a reset. It lists the three busiest
    /// hosts, because the answer to "what is fetching while I scroll" is a name, not a spread of
    /// numbers.</summary>
    public static string Line()
    {
        lock (Gate)
        {
            if (_total == 0 && _runOpened == 0 && _closed == 0)
                return "net quiet";

            var busiest = ByHost.OrderByDescending(h => h.Value)
                                .Take(3)
                                .Select(h => $"{h.Key} {h.Value}");
            var asked = ByPath.OrderByDescending(p => p.Value)
                              .Take(3)
                              .Select(p => $"{p.Key} {p.Value}");
            var sockets = Opened.Values.Sum();
            var line = $"NET {_total} ({string.Join(", ", busiest)}) [{string.Join(", ", asked)}] conn +{sockets}/-{_closed}";

            _total = 0;
            _closed = 0;
            ByHost.Clear();
            ByPath.Clear();
            Opened.Clear();
            return line;
        }
    }

    /// <summary>The periodic line, written on its own timer, so that a quiet app which is still talking
    /// to a server is caught. Nothing is written while there is nothing to say.</summary>
    private static void Report(string tag)
    {
        string line;
        lock (Gate)
        {
            if (_total == 0 && Opened.Count == 0 && _closed == 0)
                return;
        }

        line = Line();
        Say($"[{tag}-diag] {line}");
    }

    /// <summary>The run's full account, written when the process ends. The counts are the point. A route
    /// asked once is a fact about the run. A route asked four hundred times points to a design
    /// problem.</summary>
    private static void Final()
    {
        lock (Gate)
        {
            if (_runTotal == 0 && Failures.Count == 0)
                return;

            var text = new StringBuilder($"[net-diag] {_runTotal} requests this run over {_runOpened} connections ({_runClosed} closed)\n");

            text.Append("  by host:\n");
            foreach (var (host, n) in RunByHost.OrderByDescending(h => h.Value))
                text.Append($"  {n,6}x  {host}\n");

            text.Append("  by route:\n");
            foreach (var (path, n) in RunByPath.OrderByDescending(p => p.Value).Take(25))
                text.Append($"  {n,6}x  {path}\n");

            if (Failures.Count > 0)
            {
                text.Append("  failures:\n");
                foreach (var (reason, n) in Failures.OrderByDescending(f => f.Value))
                    text.Append($"  {n,6}x  {reason}\n");
            }

            Say(text.ToString());
        }
    }

    /// <summary>The debugger's output window is where this is read. A run started from a terminal has no
    /// debugger. <c>Said.Line</c> handles both cases, and each line goes to one of them at a
    /// time.</summary>
    private static void Say(string line) => RPM.Core.Diagnostics.Said.Line("net", line);
}

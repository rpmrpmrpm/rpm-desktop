using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using RPM.Core.Diagnostics;

namespace RpmScope.Collector;

/// <summary>
/// Information that the runtime has and the app cannot get about itself.
///
/// <para>A stall often ends at a thread that is not running and has no managed frames to inspect.
/// .NET has no supported way for a process to suspend one of its own threads and walk its stack, so
/// a diagnostic inside RPM cannot see RPM's worst failure. This runs in a different process,
/// attaches over the diagnostics pipe, and so still reports when RPM does not.</para>
///
/// <para>Each provider below gives information that the app cannot collect from inside.</para>
/// </summary>
internal sealed class RuntimeStream(int pid, ScopeRing ring, ScopeNames names)
{
    /// <summary>
    /// Everything the runtime is asked for, and what each answer is for.
    /// </summary>
    /// <summary>
    /// Whether to ask for stacks.
    ///
    /// <para><b>Off by default.</b> With the sample profiler attached, the runtime is SUSPENDED about
    /// 618 times a second: every thread stops, for about 22% of wall time, reason
    /// <c>SuspendOther</c>. The profiler causes this, because it stops all threads to walk stacks. A
    /// diagnostic tool must not slow the app that much, and a stall measured with it attached is
    /// partly caused by it.</para>
    ///
    /// <para>So turn stacks on only when a hang cannot be explained otherwise. Everything else has
    /// almost no cost. <c>RPM_SCOPE_STACKS=1</c>, or <c>--stacks</c>.</para>
    /// </summary>
    public static bool Stacks { get; set; } =
        Environment.GetEnvironmentVariable("RPM_SCOPE_STACKS") is "1" or "true";

    private static List<EventPipeProvider> Providers()
    {
        var providers = new List<EventPipeProvider>();

        if (Stacks)
        {
            // Managed stacks for every thread, taken from outside: the only way to see a thread that
            // is not responding. Costs about a fifth of the app's wall time; see the note on Stacks.
            providers.Add(new("Microsoft-DotNETCore-SampleProfiler", EventLevel.Informational));
        }

        providers.AddRange(
        [
        new(
            "Microsoft-Windows-DotNETRuntime",
            EventLevel.Informational,
            (long)(
                ClrTraceEventParser.Keywords.GC |            // the suspensions: every thread stopped
                ClrTraceEventParser.Keywords.Contention |    // lock storms outside Gate
                ClrTraceEventParser.Keywords.Threading |     // pool adjustment and queue depth
                ClrTraceEventParser.Keywords.WaitHandle |    // WHICH handle a blocked thread is on
                ClrTraceEventParser.Keywords.Exception)),

        new("System.Net.Http", EventLevel.Informational),
        new("System.Net.Sockets", EventLevel.Informational),

        // Counters need every keyword as well as the interval. With a zero mask the provider is
        // enabled but sends nothing, which looks the same as a provider that does not exist.
        new(
            "System.Runtime",
            EventLevel.Informational,
            keywords: -1,
            arguments: new Dictionary<string, string> { ["EventCounterIntervalSec"] = "1" }),
        ]);

        return providers;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var client = new DiagnosticsClient(pid);

        using var session = client.StartEventPipeSession(Providers(), requestRundown: false);
        using var source = new EventPipeEventSource(session.EventStream);

        // Only stopping the session ends Process(); nothing else interrupts it.
        using var stop = ct.Register(() =>
        {
            try { session.Stop(); } catch { /* already gone, which is the outcome asked for */ }
        });

        source.Clr.GCSuspendEEStart += e =>
            Write(ScopeKind.GcSuspend, e.TimeStampRelativeMSec, e.Reason.ToString(), thread: e.ThreadID);

        source.Clr.GCRestartEEStop += e =>
            Write(ScopeKind.GcRestart, e.TimeStampRelativeMSec, null, thread: e.ThreadID);

        source.Clr.ContentionStop += e =>
            Write(ScopeKind.Contention, e.TimeStampRelativeMSec, null, (long)e.DurationNs, e.ThreadID);

        source.Clr.ThreadPoolWorkerThreadAdjustmentAdjustment += e =>
            Write(ScopeKind.PoolQueue, e.TimeStampRelativeMSec, e.Reason.ToString(), e.NewWorkerThreadCount);

        source.Clr.WaitHandleWaitStart += e =>
            Write(ScopeKind.WaitHandle, e.TimeStampRelativeMSec, e.WaitSource.ToString(), thread: e.ThreadID);

        // The rest by name, because these providers have no strongly typed parser here and only one
        // field of each payload is needed.
        source.Dynamic.All += e =>
        {
            switch (e.ProviderName)
            {
                case "Microsoft-DotNETCore-SampleProfiler":
                    Write(ScopeKind.Sample, e.TimeStampRelativeMSec, null, thread: e.ThreadID);
                    break;

                case "System.Net.Http" when e.EventName == "RequestStart":
                    Write(ScopeKind.HttpRequest, e.TimeStampRelativeMSec, Payload(e, "host"), thread: e.ThreadID);
                    break;

                case "System.Net.Http" when e.EventName == "ConnectionEstablished":
                    Write(ScopeKind.HttpConnection, e.TimeStampRelativeMSec, Payload(e, "host"), thread: e.ThreadID);
                    break;

                case "System.Runtime" when e.EventName == "EventCounters":
                    var (counter, value) = Counter(e);
                    Write(ScopeKind.Counter, e.TimeStampRelativeMSec, counter, (long)(value * 1000));
                    break;
            }
        };

        await Task.Run(source.Process, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// One counter reading, from the two nested payloads that EventCounters uses.
    ///
    /// <para>The counter's name and value are not fields of the event. The event has exactly one
    /// field, named with the EMPTY STRING; inside it is a structure with one entry, "Payload"; inside
    /// THAT are Name, Mean and the rest. Reading "Name" at the top level returns nothing, and every
    /// counter then has no name and a value of zero, which looks like a provider that is not
    /// enabled.</para>
    ///
    /// <para>A rate counter reports <c>Increment</c> and a mean counter reports <c>Mean</c>. The code
    /// does not check which kind a counter is: it uses whichever value is present.</para>
    ///
    /// <para>The value is stored in thousandths, so a record's field stays an integer.</para>
    /// </summary>
    private static (string? Name, double Value) Counter(TraceEvent e)
    {
        try
        {
            if (e.PayloadNames.Length == 0 ||
                e.PayloadValue(0) is not IDictionary<string, object> outer ||
                !outer.TryGetValue("Payload", out var nested) ||
                nested is not IDictionary<string, object> fields)
            {
                return (null, 0);
            }

            var name = fields.TryGetValue("Name", out var n) ? n?.ToString() : null;

            object? raw = null;
            if (fields.TryGetValue("Mean", out var mean))
                raw = mean;
            else if (fields.TryGetValue("Increment", out var increment))
                raw = increment;

            return (name, raw is null ? 0 : Convert.ToDouble(raw, System.Globalization.CultureInfo.InvariantCulture));
        }
        catch
        {
            // If the payload shape changes in a new runtime, only this counter is lost, not the stream.
            return (null, 0);
        }
    }

    private static string? Payload(TraceEvent e, string field)
    {
        try
        {
            return e.PayloadByName(field)?.ToString();
        }
        catch
        {
            // If the payload shape changes in a new runtime, the rest of the stream must not be lost.
            // The record still has its time and its kind.
            return null;
        }
    }

    /// <summary>
    /// Writes one runtime event in the app's own format.
    /// </summary>
    /// <remarks>
    /// TraceEvent reports milliseconds since the session began; the ring uses monotonic nanoseconds
    /// shared by the two processes. The offset is measured once, at the first event, not assumed: a
    /// session's zero is when the session started, and no other code knows when that was.
    /// </remarks>
    private void Write(ScopeKind kind, double relativeMs, string? name, long a = 0, int thread = 0)
    {
        _origin ??= ScopeClock.Nanos() - (long)(relativeMs * 1_000_000);

        ring.Write(new ScopeRecord
        {
            Nanos = _origin.Value + (long)(relativeMs * 1_000_000),
            Kind = (ushort)kind,
            Thread = (ushort)(thread & 0xFFFF),
            NameId = names.Id(name),
            A = a,
        });
    }

    private long? _origin;
}

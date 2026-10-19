using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;

namespace RPM.Desktop.Services.Shell;

/// <summary>
/// Finds where an exception was thrown. The debugger's output window reports every first-chance exception
/// by type and by assembly, and nothing else. For example, "Exception thrown: 'System.IO.IOException' in
/// System.Private.CoreLib.dll" gives the type and the assembly, but not the code that threw it. This class
/// puts the stack under that message. A caught and handled throw can then be traced to the line that
/// caused it. That line can be fixed. If it is a wait that ends normally, it can be rewritten so that it
/// does not throw.
///
/// <para>Off unless <c>RPM_EXCEPTION_DIAG=1</c> is in the environment. A first-chance handler runs on every
/// throw in the process, including the ones deep inside the framework's own retry loops. So this is a tool
/// to switch on while investigating, not something an ordinary run should carry.</para>
///
/// <para>Each distinct site is printed once and then only counted. Repetition is what makes the output
/// window unreadable. A diagnostic that repeats itself would become part of the problem it was made to
/// find. The totals are written when the process ends.</para>
/// </summary>
internal static class ExceptionTrace
{
    public static readonly bool On =
        RPM.Core.Diagnostics.DevSwitch.Read("RPM_EXCEPTION_DIAG") is "1" or "true" ||
        Environment.GetCommandLineArgs().Contains("--exception-diag");

    /// <summary>
    /// Counting without tracing: the total and the types, no stacks, nothing said.
    ///
    /// <para><b>Why there are two levels.</b> A first-chance handler that reads a stack trace is expensive
    /// enough to change what it measures. The wall's stall line needs to know how many throws happened
    /// while the thread was held, not where they came from. Under a debugger every throw is a round trip
    /// to the debugger: the process stops, the debugger is told, and the process starts again. A scroll
    /// that throws four hundred times would stop four hundred times. That shows up as a hang that only
    /// happens under F5. The app itself does not see that cost. It can only count the throws and put the
    /// number where the stall is reported. Counting costs one increment.</para>
    /// </summary>
    private static bool Counting => Hunting || RPM.Core.Diagnostics.Scope.On;

    private static long _thrown;
    private static long _thrownSaid;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> ByType =
        new(StringComparer.Ordinal);

    /// <summary>The same throws, counted by the line that made them rather than by their type.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> BySite =
        new(StringComparer.Ordinal);

    /// <summary>How many throws a second are worth reading a stack for.
    /// <para><b>Fewer under a debugger, and it is not caution.</b> A capture of a live hang showed this
    /// handler's own stack walk in progress. The walk was <c>DeliverFirstChanceNotification</c>, then
    /// <c>GetStackFramesInternal</c>, then <c>Debugger::GetILOffsetFromNative</c>. That is a round trip
    /// to the debugger for each frame. So the instrument costs the most exactly where throws are
    /// already expensive. A throw storm is when it is most needed, and also when it would cost most.
    /// Five a second is still enough to name the site that is causing the storm, because that site
    /// throws hundreds of times.</para>
    /// <para><b>Fewer again when nobody asked.</b> The ring is open in every run. So this handler is
    /// installed in every run, including release builds on users' machines and every debug session
    /// started with F5. Twenty stack walks a second is the budget when hunting. Runs that are not
    /// hunting must not pay for that. Without <c>RPM_EXCEPTION_DIAG</c> or <c>RPM_WALL_DIAG</c>, the
    /// budget is five a second. Under a debugger the budget is zero. The type is still counted and
    /// still goes on the ring. The round trip per frame is not paid by a run that did not ask for the
    /// sites. Set either switch to get sites under a debugger.</para>
    /// </summary>
    private static int SitesPerSecond => Hunting
        ? (Debugger.IsAttached ? 5 : 20)
        : (Debugger.IsAttached ? 0 : 5);

    /// <summary>Whether a switch asked for this handler, as opposed to it being installed because the
    /// ring is open.</summary>
    private static bool Hunting => On || Presentation.Main.Library.WallDiag.On;

    private static long _siteSecond = -1;
    private static int _sitedThisSecond;

    /// <summary>How much stack to print. Enough to cross out of the framework and into our own code,
    /// which is the only frame anybody reading this is looking for.</summary>
    private const int Frames = 14;

    /// <summary>Reading a stack trace runs code, and code throws; a handler that re-entered itself would
    /// recurse until the stack ran out. One flag per thread, because the throws are on many.</summary>
    [ThreadStatic]
    private static bool _inside;

    private static readonly object _lock = new();
    private static readonly Dictionary<string, int> _seen = new(StringComparer.Ordinal);
    private static bool _installed;

    public static void Install()
    {
        if (!Counting || _installed)
            return;
        _installed = true;

        AppDomain.CurrentDomain.FirstChanceException += OnThrown;
        if (!On)
            return;   // counting only: no stacks, no tally timer, nothing said

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Report();
        _beat = new Timer(_ => Tally(), null, Beat, Beat);

        Say("[exception-diag] on: every first-chance throw is traced to its first appearance");
    }

    /// <summary>
    /// What the process threw since the last time anybody asked, for the stall line.
    ///
    /// <para>Read this number against the length of the stall next to it. Four hundred throws in a second
    /// is not an error rate. It is a sign of a design that throws often. Under a debugger, it is also
    /// four hundred stops.</para>
    /// </summary>
    public static string Line()
    {
        if (!_installed)
            return "throws unwatched";

        var now = Interlocked.Read(ref _thrown);
        var since = now - _thrownSaid;
        _thrownSaid = now;

        if (since == 0)
            return "throws 0";

        var top = ByType
            .Select(p => (Type: p.Key, p.Value))
            .OrderByDescending(t => t.Value)
            .Take(3)
            .Select(t => $"{t.Type} {t.Value}");

        // The worst site is shown too, because the type alone is not enough. For example,
        // TaskCanceledException says that the app cancels things on purpose. It does not say which part
        // of the app is doing it several times a second.
        var worst = BySite.IsEmpty ? null : BySite.OrderByDescending(p => p.Value).First().Key;
        var mostly = worst is null ? "" : $", mostly {worst} {BySite[worst]}";

        return $"throws {since} (run total {now}: {string.Join(", ", top)}{mostly})";
    }

    /// <summary>How often the running total is written out. Each site's stack is printed once and then only
    /// counted. This keeps the output readable. But it also means four throws and four hundred throws
    /// look the same in the log. This total is the number to compare before and after a change.</summary>
    private static readonly TimeSpan Beat = TimeSpan.FromSeconds(30);

    private static Timer? _beat;

    /// <summary>What the process has thrown so far, by type, on one line. Silent while nothing has
    /// changed since the last one, so a quiet app writes nothing.</summary>
    private static int _tallied;

    private static void Tally()
    {
        string line;
        lock (_lock)
        {
            var total = _seen.Values.Sum();
            if (total == _tallied)
                return;

            _tallied = total;

            var byType = _seen
                .GroupBy(p => p.Key[..p.Key.IndexOf('\n')])
                .Select(g => (Type: g.Key[(g.Key.LastIndexOf('.') + 1)..], Count: g.Sum(p => p.Value)))
                .OrderByDescending(t => t.Count);

            line = $"[exception-diag] {total} throws over {_seen.Count} sites: " +
                   string.Join(", ", byType.Select(t => $"{t.Type} {t.Count}"));
        }

        Say(line);
    }

    /// <remarks>
    /// <b>The flag is set before anything else happens, and everything after it is inside the try.</b>
    /// This handler runs on every throw in the process. Any throw that the handler makes itself calls the
    /// handler again. If the flag were not set yet, that call would throw again, without limit, until the
    /// stack ran out. Growing a dictionary allocates memory, and allocation throws when no memory is left.
    /// So there is no place in this method where the order can be relaxed. Set the flag first, do the
    /// work second, and reset the flag in a finally block.
    /// </remarks>
    private static void OnThrown(object? sender, FirstChanceExceptionEventArgs e)
    {
        if (_inside)
            return;
        _inside = true;

        try
        {
            // The counting half, and it runs whichever level is on: an increment and a name, no stack.
            Interlocked.Increment(ref _thrown);
            ByType.AddOrUpdate(e.Exception.GetType().Name, 1, (_, had) => had + 1);

            // This also goes into the ring on the counting-only path. The number that matters is how many
            // throws landed inside a stall. That is a question about when they happened, not where they
            // came from, and a tally cannot answer it. One stack walk gives both the site and, for a
            // site's first throw, its chain.
            var trace = Affordable() ? new StackTrace(fNeedFileInfo: false) : null;
            var site = trace is null ? null : SiteOf(trace);

            // The first throw from a line puts its whole chain in the ring. Every later throw from that
            // line puts only the short name. The ring is affordable here. It is a lock-free write into a
            // mapped file, with no allocation on the path. Another process reads it, so nothing here waits
            // on a debugger. That is the difference from printing. Read it back with `rpmscope throws`.
            var first = site is not null && !BySite.ContainsKey(site);

            // Counted, and NOT printed. Printing here hangs the app. Say goes to Debug.WriteLine, and each
            // Debug.WriteLine under a debugger is a round trip to the debugger. This code runs inside the
            // first-chance handler. So a line printed here makes the window longer in which a thread sits
            // at a point that the debugger treats as unsafe. A collection cannot suspend the thread in that
            // window. A dozen distinct sites at startup was enough to cause the hang. Stacks belong on the
            // traced level, and the counting level says nothing on purpose.
            if (site is not null)
                BySite.AddOrUpdate(site, 1, (_, had) => had + 1);

            RPM.Core.Diagnostics.Scope.Throw(
                e.Exception.GetType().Name,
                first ? Chain(trace!) : site);

            if (!On)
                return;

            var stack = Trim(new StackTrace(fNeedFileInfo: true));
            var key = e.Exception.GetType().FullName + "\n" + stack;

            lock (_lock)
            {
                if (_seen.TryGetValue(key, out var count))
                {
                    _seen[key] = count + 1;
                    return;
                }
                _seen[key] = 1;
            }

            Say($"[exception-diag] {e.Exception.GetType().Name}: {e.Exception.Message}\n{stack}");
        }
        catch
        {
            // A diagnostic that can take the app down is worse than no diagnostic.
        }
        finally
        {
            _inside = false;
        }
    }

    /// <summary>
    /// Whether this throw is inside the second's budget for reading a stack.
    ///
    /// <para><b>Why a budget rather than a flag.</b> Counting is free: an increment and a name, with no
    /// stack. The reason is that a handler which reads a stack changes what it measures. A stall line needs
    /// how many throws happened, not where they came from. That reason still holds, and the count still
    /// comes first on the line. But the next question is nearly always which line, and a tally cannot
    /// answer that. For example, a count of two hundred cancellations shows that the app cancels by design.
    /// It does not show which part of the design does it.</para>
    ///
    /// <para>So the stack is read for the first twenty throws of each second and no more. A background rate
    /// of three a second is named in full. A scroll that throws four hundred times is named for the first
    /// twenty and only counted for the rest. The cost has a ceiling that does not rise when the app
    /// misbehaves, and that is when a diagnostic must not cost more.</para>
    /// </summary>
    private static bool Affordable()
    {
        var second = Environment.TickCount64 / 1000;
        if (Interlocked.Exchange(ref _siteSecond, second) != second)
            Interlocked.Exchange(ref _sitedThisSecond, 0);

        return Interlocked.Increment(ref _sitedThisSecond) <= SitesPerSecond;
    }

    /// <summary>The first frame in a trace that belongs to this app rather than to the framework.
    /// <para>Internal because the choice of frame is the whole content of the answer and wants pinning
    /// by a test: the frame directly under a throw is nearly always <c>ThrowHelper</c> or a state
    /// machine's <c>MoveNext</c>, and neither is a place anybody can go and look.</para>
    /// <para><b>Two frames, not one.</b> Taking only the nearest <c>RPM.*</c> frame gives
    /// <c>Program.Main</c> most often. That is true, because everything on the drawing thread is under
    /// it. But it names no code that anybody can change. The frame that actually threw is often in the
    /// renderer or the toolkit. It belongs to neither <c>RPM.*</c> nor the framework. So the nearest
    /// NON-FRAMEWORK frame is taken as well and both are reported, nearest first:
    /// <c>NativeDispatcher.DispatchItems &lt;- Program.Main</c> says what threw and on whose behalf.
    /// One name alone shows only half of that.</para>
    /// </summary>
    internal static string? SiteOf(StackTrace trace)
    {
        // The nearest frame to the throw that is not the framework's, and the nearest that is ours.
        // Usually the same one; when they differ, both are worth having and neither is enough alone.
        string? near = null;
        string? ours = null;

        foreach (var frame in trace.GetFrames())
        {
            if (Frame(frame) is not var (type, member))
                continue;

            if (type.StartsWith("RPM.Desktop.Services.Shell.ExceptionTrace", StringComparison.Ordinal))
                continue;

            var mine = type.StartsWith("RPM.", StringComparison.Ordinal);

            if (!mine && (type.StartsWith("System.", StringComparison.Ordinal) ||
                          type.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal)))
                continue;

            near ??= Named(type, member);

            if (!mine)
                continue;

            ours = Named(type, member);
            break;
        }

        if (ours is null || near is null)
            return ours ?? near;

        return near == ours ? ours : $"{near} <- {ours}";
    }

    /// <summary>
    /// The declaring type and the method name of one frame, or null where the frame names no managed
    /// method.
    ///
    /// <para>DiagnosticMethodInfo is used rather than <c>StackFrame.GetMethod()</c>. The published build
    /// is Native AOT, and there GetMethod() returns null for every frame, even with
    /// IlcGenerateStackTraceData set. That setting keeps the names that the stack trace prints, and this
    /// reads the same table. So it works under AOT and under the debugger alike.</para>
    ///
    /// <para>Getting this wrong would break the whole class, not one detail of it. Every frame would read
    /// as having no method, and the checks above would skip every frame. The shipped build would then have
    /// no diagnostics, while a build on a developer's machine has all of them.</para>
    /// </summary>
    private static (string Type, string Member)? Frame(StackFrame frame)
        => DiagnosticMethodInfo.Create(frame) is { DeclaringTypeName: { } type } named
            ? (type, named.Name)
            : null;

    /// <summary>
    /// A frame written the way it was written in the source.
    ///
    /// <para>The namespace is removed, because it is the same for every line being compared. The
    /// compiler's own types are removed too. An async body lives in <c>Wire+&lt;SendAsync&gt;d__7</c>,
    /// whose only method is <c>MoveNext</c>. A closure lives in <c>Wire+&lt;&gt;c__DisplayClass</c>, with
    /// the written name on the METHOD instead. Left alone, every async site in the app would report itself
    /// as "MoveNext", and the tally would have one large row that means nothing.</para>
    /// </summary>
    private static string Named(string type, string member)
    {
        var shortened = type[(type.LastIndexOf('.') + 1)..];
        var host = shortened.IndexOf('+') is var plus and > 0 ? shortened[..plus] : shortened;
        if (host.StartsWith('<'))
            host = "";

        var name = Bracketed(shortened) ?? Bracketed(member) ?? member;
        return host.Length == 0 ? name : $"{host}.{name}";
    }

    /// <summary>What is between the angle brackets, which is where the compiler keeps the name that was
    /// actually typed. Null when there is nothing there, brackets with nothing in them included.</summary>
    private static string? Bracketed(string text)
    {
        var open = text.IndexOf('<');
        var close = text.IndexOf('>');
        return open >= 0 && close > open + 1 ? text[(open + 1)..close] : null;
    }

    /// <summary>
    /// The whole chain under a throw on one line, nearest first, for the ring.
    ///
    /// <para>One line, not many, because a ring record holds one name. The chain is the answer. For
    /// example, <c>BitmapImage.TryOpenSourceAsync</c> on its own says that the renderer cancelled a
    /// picture. It does not say which part of the app asked for the picture.</para>
    /// </summary>
    private static string Chain(StackTrace trace)
    {
        var chain = new StringBuilder();
        var kept = 0;

        foreach (var frame in trace.GetFrames())
        {
            if (Frame(frame) is not var (type, member))
                continue;

            if (type.StartsWith("RPM.Desktop.Services.Shell.ExceptionTrace", StringComparison.Ordinal) ||
                type.StartsWith("System.Runtime.CompilerServices", StringComparison.Ordinal) ||
                type.StartsWith("System.Runtime.ExceptionServices", StringComparison.Ordinal) ||
                type.StartsWith("System.Threading.ExecutionContext", StringComparison.Ordinal))
                continue;

            if (chain.Length > 0)
                chain.Append(" < ");
            chain.Append(Named(type, member));

            if (++kept >= Frames)
                break;
        }

        return chain.Length == 0 ? "(no managed frames)" : chain.ToString();
    }

    /// <summary>The stack from the throw outwards, minus the handler's own frames and minus the runtime
    /// plumbing that sits between every await and the method that wrote it.</summary>
    private static string Trim(StackTrace trace)
    {
        var text = new StringBuilder();
        var kept = 0;

        foreach (var frame in trace.GetFrames())
        {
            if (Frame(frame) is not var (type, member))
                continue;

            // Us, and the machinery that got here: neither is where the throw came from.
            if (type.StartsWith("RPM.Desktop.Services.Shell.ExceptionTrace", StringComparison.Ordinal) ||
                type.StartsWith("System.Runtime.ExceptionServices", StringComparison.Ordinal) ||
                type.StartsWith("System.Runtime.CompilerServices", StringComparison.Ordinal) ||
                type.StartsWith("System.Threading.ExecutionContext", StringComparison.Ordinal))
                continue;

            var file = frame.GetFileName();
            var where = file is null ? "" : $"  {Path.GetFileName(file)}:{frame.GetFileLineNumber()}";
            text.Append("    at ").Append(type).Append('.').Append(member).Append(where).Append('\n');

            if (++kept >= Frames)
                break;
        }

        return text.Length == 0 ? "    (no managed frames)" : text.ToString().TrimEnd('\n');
    }

    /// <summary>What the run threw, most often first. The counts are the point. A site that threw once
    /// is a fact about that run. A site that threw four hundred times points to a design problem.</summary>
    private static void Report()
    {
        List<KeyValuePair<string, int>> sites;
        lock (_lock)
        {
            if (_seen.Count == 0)
                return;
            sites = _seen.OrderByDescending(p => p.Value).ToList();
        }

        var text = new StringBuilder($"[exception-diag] {sites.Count} throw sites this run:\n");
        foreach (var (key, count) in sites)
        {
            var split = key.IndexOf('\n');
            text.Append($"  {count,5}x  {key[..split]}\n{key[(split + 1)..]}\n");
        }
        Say(text.ToString());
    }

    /// <summary>The debugger's output window is where this is read. A run started from a terminal has no
    /// debugger. <c>Said.Line</c> handles both cases, and each line goes to one of them at a
    /// time.</summary>
    private static void Say(string line) => RPM.Core.Diagnostics.Said.Line("throws", line);
}

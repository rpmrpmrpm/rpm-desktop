using System.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;
using RPM.Core.Diagnostics;
using RpmScope.Collector;

var pid = 0;
for (var i = 0; i < args.Length - 1; i++)
    if (args[i] is "--pid" or "-p")
        _ = int.TryParse(args[i + 1], out pid);

if (pid == 0)
{
    // Find the process instead of requiring a pid. There is only one rpm, so the user does not
    // need to look up its pid before investigating a stall.
    var found = Process.GetProcessesByName("rpm").FirstOrDefault()
             ?? Process.GetProcessesByName("RPM.Desktop").FirstOrDefault();

    if (found is null)
    {
        Console.Error.WriteLine("no rpm process found; pass --pid");
        return 1;
    }

    pid = found.Id;
}

if (args.Contains("--stacks"))
    RuntimeStream.Stacks = true;

var dir = RPM.Core.Diagnostics.Scope.Dir;

// The collector has its OWN names table, next to its own ring.
//
// Do not share the app's table: two processes appending to one file under two separate locks, each
// with its own count, give the same id to different words, and the reader then shows wrong names
// (for example a counter name as an exception type).
//
// A record is resolved against the table next to the ring it came from, and neither process
// touches the other's file.
// Named after the process it is WATCHING, not after this one, so a reader that has found
// the app's ring knows which collector ring belongs with it.
using var names = ScopeNames.Fresh(Path.Combine(dir, $"collector.{pid}.names"));
// Sized for what is collected. With stacks, the sample profiler alone writes about 230,000 records
// in three minutes on an idle app, enough to fill a small ring and overwrite the stacks of a long
// hang before anyone reads them. Capturing a hang is the main purpose, so with stacks the ring must
// hold one. WITHOUT stacks the stream is a few hundred records a minute, and a quarter of a
// gigabyte per run would only waste space in the user's cache directory.
var slots = int.TryParse(Environment.GetEnvironmentVariable("RPM_SCOPE_COLLECTOR_SLOTS"), out var asked) && asked > 0
    ? asked
    : RuntimeStream.Stacks ? 4_194_304 : 262_144;   // 256 MB with stacks, 16 MB without

using var ring = ScopeRing.Create(Path.Combine(dir, $"collector.{pid}.ring"), slots);

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stopping.Cancel();
};

Console.WriteLine($"[collector] attached to {pid}, writing {dir}/collector.{pid}.ring");
Console.WriteLine(
    RuntimeStream.Stacks
        ? "[collector] suspensions, contention, wait handles, sockets, counters, AND STACKS " +
          "(the profiler stops the runtime ~600 times a second: use it to catch a hang, not to watch an app)"
        : "[collector] suspensions, contention, wait handles, sockets, counters. --stacks adds stacks, at a fifth of the app's wall time");

var threads = new ThreadCpu(pid, ring, names);
var runtime = new RuntimeStream(pid, ring, names);

try
{
    await Task.WhenAll(threads.RunAsync(stopping.Token), runtime.RunAsync(stopping.Token));
}
catch (OperationCanceledException)
{
    // Stop was requested. Not a fault.
}
catch (ServerNotAvailableException)
{
    Console.Error.WriteLine("[collector] the app went away");
}
catch (EndOfStreamException)
{
    // The session ended, which is how a stop looks from the reading side.
}
catch (ServerErrorException e)
{
    // The runtime refused the session. Usually the process has already exited: an app that
    // restarts itself during start-up gets a new pid, and the old pid no longer accepts sessions.
    // Print a plain message and exit, instead of an unhandled exception with a stack trace from
    // inside the diagnostics client.
    Console.Error.WriteLine($"[collector] the runtime would not open a session: {e.Message}");
    Console.Error.WriteLine("[collector] is that pid still alive? a fresh --pid usually fixes it");
    return 2;
}

Console.WriteLine("[collector] stopped");
return 0;

// Rings that belong to processes that have exited.
//
// The file name has the pid so that two writers never share one file, but then every run leaves a
// ring behind, and with stacks on that ring is a quarter of a gigabyte. A pid that no longer runs
// cannot be writing, so its ring is complete and can be deleted. The last run's ring is kept,
// because reading a trace after the app has closed is a main use.
static void Sweep(string directory)
{
    try
    {
        var mine = Directory.GetFiles(directory, "collector.*")
            .GroupBy(f => Path.GetFileName(f).Split('.') is [_, var id, ..] ? id : "")
            .Where(g => g.Key.Length > 0 && int.TryParse(g.Key, out _))
            .OrderByDescending(g => g.Max(f => File.GetLastWriteTimeUtc(f)))
            .Skip(1);

        foreach (var run in mine)
        {
            if (int.TryParse(run.Key, out var owner) && Alive(owner))
                continue;

            foreach (var file in run)
            {
                try { File.Delete(file); } catch { /* somebody else is reading it; leave it */ }
            }
        }
    }
    catch
    {
        // A cleanup failure must never stop the collector from starting.
    }
}

static bool Alive(int pid)
{
    try
    {
        using var _ = Process.GetProcessById(pid);
        return true;
    }
    catch
    {
        return false;
    }
}

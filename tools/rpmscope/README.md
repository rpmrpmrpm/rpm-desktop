# rpmscope

Reads what RPM writes down about itself: what ran, what waited, what threw, and why a frame was late.

**[HELP.md](HELP.md) is the one to read.** It has every command with examples, what each row means,
how to read a stall, and the known traps. This file is the short version.

## The two rings

RPM writes `~/Library/Caches/rpm/scope.ring` from inside itself, and the collector writes
`scope.collector.ring` from a second process. Both are mapped files of fixed 64-byte slots on one
system-wide clock, so records from either side can be laid against each other. `scope.names` holds
the text; a record carries only an index into it.

`RPM_SCOPE_DIR` moves all three. `RPM_SCOPE=0` in RPM's environment turns the app's half off. There
is nothing to turn on, because a diagnostic that has to be switched on before a fault is off when the
fault happens.

Reading is free: the reader maps the files read-only, never writes, never connects, and cannot slow
RPM down or be noticed by it. It keeps working while RPM is frozen (the macOS beachball).

## Install

Nothing to install: `scripts/rpmscope` builds its own virtualenv the first time it is run.

```sh
scripts/rpmscope top
```

## Commands

```sh
rpmscope top                    # one snapshot: holds, waits, stalls, throws, last 10s
rpmscope stalls --over 150      # every frame over the bar, worst first, one line each
rpmscope why 41                 # the chain for one stall, from `stalls`
rpmscope throws                 # exceptions by type, by line, and by WHO ASKED
rpmscope log --channel walldiag # the app's own diagnostic lines
rpmscope catch --stuck 1500     # wait for the ui to actually be gone, then sample it once
```

`throws` answers the question a tally cannot. The type ("206 TaskCanceledException") is the half
that is never in doubt. **Asked for by** is the half that matters, and it cannot come off a stack:
work that a library hands to the pool throws with no frame of ours on its stack. It is carried
in an `AsyncLocal` instead, so it survives the await that loses the caller.

`log` is where the app's own diagnostic lines go. They do not go to the debugger's output
window, because **every line reaching an attached debugger is a round trip to it**. A diagnostic that
writes one per realized row would stop the process a few hundred times a second, which is the kind of
fault this tool exists to find. In the ring a line costs one record and lands on the same clock as the stalls
it is about, so the two are read together.

`catch` waits for the drawing thread to actually be missing, and then takes ONE `sample(1)`.
`ScopeShell`'s watchdog keeps writing while the thread is gone, so "stuck NOW" is a fact that another
process can read for free. It does not use processor use as a trigger: startup burns several cores and
passes through single-core phases that look like a stall. It does not use the debugger: pausing it
lengthens the stall being looked at. It makes one capture and never loops, because a watcher that
re-attaches on every launch changes the behaviour of the app it watches.

`why` is the one that answers a question:

```
frame 8 overran 19,380ms  (cpu 6ms, the thread was NOT running)
  read    held 19,371ms by GetAlbumCloudRollup (cpu 4ms)
  via     CloudBadgeOf
  via     RefreshBadges
  via     OnSharedShelfChanged
  throws  2 throws (IOException)
```

**Read the processor number first.** It decides which fix is worth trying at all. Nineteen seconds of
wall with nineteen of processor is work, for example a query with no index under it, and it can be
made faster. Nineteen seconds of wall with four milliseconds is a thread that was NOT RUNNING, and
making its work faster would do nothing. The two look the same from outside, so only the measured
processor time tells you which one a stall was.

Then read the chain from the bottom up: the last line is who started it, and usually where the fix
goes. Work that merely happened during the frame is left out on purpose. A chain that names
everything is a dump again.

## The window

```sh
scripts/rpmscope view                    # or: scripts/rpmscope view --seconds 120
```

`scripts/rpmscope` makes the virtualenv, installs into it, and fetches Qt the first time the window
is asked for. There is nothing to activate. To type it from anywhere:

```sh
ln -s "$PWD/scripts/rpmscope" /usr/local/bin/rpmscope
```

The timeline is on top. It has a row each for the user's presses, ui stalls, gate holds, gate waits, suspensions,
contention, requests and throws, all on one axis, because a hold of two hundred milliseconds is
unremarkable while nobody is looking at the screen and is the whole problem in the middle of a scroll.
Counters are underneath on the same axis. "Now" is at the left, and it is correct while rpm is
frozen. Click a stall and its chain appears at the right.

**Live** and the range buttons are two different settings. The buttons say how WIDE the window is;
Live says whether the right edge follows the present. Held still, a wider window grows from the right
edge, so you can ask for "half an hour, ending where I am looking".

Live keeps the right edge at the present, and dragging or zooming turns it off by itself. The two
are the same gesture fighting, and with both on, the view would snap back before the hand came off
it. Pressing Live again returns to the present at whatever width is on screen, because a width
chosen by zooming is kept. The ring already is the history, so scrubbing costs nothing. The window
reads only what has arrived since the last look, twenty times a second, and holds a fifteen-minute
window of its own.

## The collector

```sh
dotnet run --project tools/RpmScope.Collector -- --pid <rpm>     # or no --pid, it finds rpm
```

It supplies the half that RPM cannot see about itself: managed stack samples for every thread at
a kilohertz, collector suspensions, lock contention outside `Gate`, which handle a blocked thread is
on, and the `System.Runtime` counters. It is a second process for one reason: .NET has no supported
way for a process to suspend one of its own threads and walk it, and a stalled thread is exactly the
one you need a stack for.

**Stacks are off by default.** With the sample profiler on, measurements show the runtime SUSPENDED
618 times a second, with every thread stopped for 22.3% of wall time. That is the profiler itself: it
stops the world to walk stacks. With it off, suspensions are zero and the whole collector produces a
few hundred records a minute. So the instrument stays unnoticeable, and you add stacks when a hang
cannot be attributed any other way:

```sh
rpmscope-collector --stacks     # or RPM_SCOPE_STACKS=1
```

Use it to catch a hang, not to watch an app. A stall measured under it is partly a stall caused by it.

Per-thread processor time needs the target's mach task port, so it wants `sudo`. Without it the
collector says so once and carries on with everything else.

## Tests

```sh
dotnet test RPM.Core.Tests/RPM.Core.Tests.csproj --filter FullyQualifiedName~ScopeFormatTests
cd tools/rpmscope && ./.venv/bin/python -m pytest tests/ -q
```

The .NET test writes a fixture with every field set to a number that could not be arrived at by
accident, and the Python tests assert the same numbers come back. The record layout is shared between
two languages and no compiler checks the agreement, so that pair of tests is the agreement.

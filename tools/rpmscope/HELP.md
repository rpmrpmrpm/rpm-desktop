# rpmscope

Reads what RPM writes down about itself while it is running: what stalled, what held a lock, what
threw, and what the app said at the time. Everything is read out of a memory-mapped ring from another
process, so **it keeps working while the app is beachballed**, which is the only moment any of it
matters.

```sh
scripts/rpmscope view                  # the window. leave it open while you work
scripts/rpmscope catch --stuck 1500    # arm it, go use the app, get the stacks from the hang
scripts/rpmscope throws                # what is throwing, and which of ours asked for it
```

`scripts/rpmscope` builds its own virtualenv on first run. Nothing to install, nothing to activate.
To type it from anywhere: `ln -s "$PWD/scripts/rpmscope" /usr/local/bin/rpmscope`.

---

## How it is put together

| piece | what it is |
| --- | --- |
| `RPM.Core/Diagnostics/ScopeRing.cs` | single-writer lock-free ring in a mapped file. Fixed 64-byte records, one cache line each. The sequence number is written last with a release store, and that is the entire synchronisation. |
| `ScopeNames.cs` | append-only name table. A record carries an integer, never text. Text on the write path would need an allocation, and an allocation would make the instrument part of what it measures. |
| `ScopeClock.cs` | `CLOCK_UPTIME_RAW`, which is system-wide, so two processes' records lay against each other with no correlation step. |
| `ScopeShell.cs` | the watchdog. Posts a stamped dispatcher item every 16ms from a plain thread and records how late it comes back. **The measurement is not taken on the thread being measured**, which is why it still reads while that thread is gone. |
| `tools/RpmScope.Collector` | second process. Supplies what RPM cannot see about itself: EE suspensions, lock contention, `System.Runtime` counters, and (opt-in) managed stacks. |
| `tools/rpmscope` | this. Reads the rings read-only. Never writes, never connects, cannot be noticed by the app. |

`RPM_SCOPE_DIR` moves the rings. `RPM_SCOPE=0` turns the app's half off. There is nothing to turn
*on*, because a diagnostic that you have to switch on before a fault is off when the fault happens.

---

## The rows

One row per record kind, all on one clock, so anything vertically aligned is concurrent. **Bars run
backwards from their stamp**: a record is written when the thing ENDED and carries its duration. Drawn
forwards, every cause would sit after its own effect.

| row | record | what it is |
| --- | --- | --- |
| **input** | `Input` | pointer, key and wheel, hooked `handledEventsToo: true` so events something already handled still land. This is the reference row: read every other row against it to see when a person did something. |
| **ui thread blocked** | `FrameOverrun` | DispatcherQueue round-trip overrun. `a` = wall time missing, `b` = the UI thread's own CPU delta over it. |
| **db gate held** | `GateHold` | a `LibraryDatabase` connection held exclusively; the name is the caller. SQLite is one writer, so a hold is every other write queued behind it. |
| **db gate waited** | `GateWait` | time queued for that connection. The name is who was **holding**, not who was waiting. |
| **EE suspended** | `GcSuspend` | `ThreadSuspend::SuspendEE`: every managed thread stopped. Needs the collector. |
| **monitor contention** | `Contention` | `Monitor` waits that actually blocked, from `ContentionStart/Stop`. Locks outside the database's own gates. |
| **http** | `HttpRequest`, `HttpConnection` | requests started and connections opened, from `System.Net.Http`'s EventSource. |
| **first-chance throws** | `Throw` | `AppDomain.FirstChanceException`, caught or not. |
| **trace lines** | `Said` | `Scope.Said`, the app's own diagnostic lines (the channels `walldiag`, `db`, `net` and `throws`). The row is only *when*; the text is in the pane below. |

---

## Commands

### `view`: the window

```sh
scripts/rpmscope view [--seconds 120]
```

- **Live** keeps the right edge at the present. **Dragging or zooming turns it off by itself**. The
  two are the same gesture fighting, and with both on, the view would snap back before your hand
  came off it.
- The **30s / 2m / 10m / 30m** buttons set the WIDTH, not whether it follows. Held still, a wider
  window grows from the right edge, so "half an hour, ending where I am looking" is askable.
- **Drag across the timeline** to measure a period. The trace lines underneath narrow to the
  selection. This is why they are in this window and not in a console.
- **Click a bar** on *ui thread blocked* for its chain.
- **Group by thread** re-lays the same records as a row per thread, ordered by how much of the window
  each accounts for.

### `catch`: get the stacks from a hang

```sh
scripts/rpmscope catch --stuck 1500 --out /tmp/rpm-stall.txt
```

Arm it, then go and use the app. It waits until the drawing thread has actually been gone that long,
takes **one** `sample(1)`, and exits.

It deliberately does not do three things:

- **It does not trigger on CPU.** Startup burns several cores and passes through single-core phases
  indistinguishable from a stall, so a threshold either fires on the launch or has to wait the launch
  out. The hang can also happen before the window is even up.
- **It does not use the debugger.** Pausing to look lengthens the stall being looked at, and done
  wrong it does not let go.
- **It does not loop.** One capture, one deadline. A watcher that re-attaches on every launch is
  silent, and it changes the behaviour of the app it watches. That looks like an intermittent fault
  and is not.

### `throws`: what is throwing, and who asked

```sh
scripts/rpmscope throws
```

```
353 throws over 36.1s (10/s):
      353  TaskCanceledException

asked for by:
       78  FetchAsync  (ArtistWallView.xaml.cs)
       16  ShowAsync   (GenreWallView.xaml.cs)

by the line that threw:
      126  BitmapImage.TryOpenSourceAsync <- Program.Main
```

Three readings, and only one of them is the answer. The **type** is the half that is never in doubt.
"353 TaskCanceledException" says that the app cancels things on purpose, and says nothing about which code asked.
The **line** is the framework's. **Asked for by** is ours, and it cannot come off a stack at all: work
that a library hands to the pool throws with no frame of yours on its stack. It is carried in an
`AsyncLocal` (`Scope.Job`), which flows through `Task.Run` and every continuation, so it survives the
await that loses the caller.

If you add a place that starts work the framework finishes, put a `Scope.Job` at the place that
**asks**, not in the helper. A job in a leaf that does not await flows nowhere.

### `log`: the app's own lines

```sh
scripts/rpmscope log --channel walldiag --tail 300
```

These lines do not go to `Debug.WriteLine`, for a reason that is also the subject of this tool:
**every line reaching an attached debugger is a round trip to it.** A diagnostic writing one per
realized row stops the process a few hundred times a second, and what it then measures is an app being
stopped by its own diagnostic. In the ring a line costs one record and lands on the same clock as the
stalls it is about.

Channels are gated per area in `.vscode/launch.json`: `RPM_WALL_DIAG` (walls), `RPM_DB_DIAG`
(database gates), `RPM_EXCEPTION_DIAG` (per-site throw stacks). `NetDiag` writes unconditionally.

### `export`: dig through it properly

```sh
scripts/rpmscope export --out /tmp/rpm-trace.json
```

Writes the ring as a Chrome/Perfetto trace. Drop it on <https://ui.perfetto.dev>. The trace stays on your
machine. Threads become tracks (thread 1 is named *drawing thread*), gate holds become slices, throws
and inputs become marks, counters become graphs. Perfetto gives you flamegraphs, search and SQL over the trace.

The live window is for watching; `export` is for digging.

### The rest

```sh
scripts/rpmscope top                  # one snapshot of the last ten seconds
scripts/rpmscope stalls --over 150    # every overrun, worst first, one line each
scripts/rpmscope why 41               # the chain for one of them
scripts/rpmscope stopped              # wall time with every thread suspended
```

---

## Reading a stall

**Read the processor number first.** It decides which fix is worth trying at all.

- Wall time **with matching CPU**: the thread was RUNNING. The work is too slow and must be made
  cheaper.
- Wall time with **no CPU**: the thread was PARKED. It was not doing the work at all, and making the
  work faster changes nothing.

They look the same from outside, so only the measured CPU tells you which one a stall was.

Then read the chain from the bottom up: the last line is who started it, and usually where the fix
goes. Work that merely happened during the frame is left out on purpose. A chain that names
everything is a dump again.

---

## Traps

These are known traps. Read them before you trust a reading.

- **`GC.GetTotalPauseDuration()` mis-attributes.** It is timed from when the collector ASKS to
  suspend, not when it gets to run, so *any* other suspension is charged to it. A line reading
  `gc held 15,715ms in 2 gen0 collections, promoted 0MB` means the world was stopped, not that
  collecting is what stopped it.
- **The EventPipe sample profiler stops the world.** `dotnet-stack`, `dotnet-trace --profile
  cpu-sampling`, and this collector's own `--stacks` all enable it. On macOS `FlushProcessWriteBuffers`
  is a mach round trip per thread, twice per suspension, so at a kilohertz it can consume the entire
  wall clock and freeze the app outright. In one measurement, 3,371 of 3,371 samples were in `SuspendEE`/`RestartEE`.
  Never leave one attached; use `sample(1)`, which needs nothing from the runtime.
- **Killing the client does not always stop the sampler.** The runtime holds the session for about 15
  seconds after the last client is gone, and it keeps suspending. A capture taken inside that window
  shows the sampler running with nothing attached, which looks like an in-process fault. Sample twice.
- **A stale ring is full of real stalls that are over.** `catch` only looks at the last two seconds
  for exactly this reason. If you read a ring by hand, check the pid in its filename.
- **Under a debugger, walking a stack costs a debugger call per frame**
  (`Debugger::GetILOffsetFromNative`). `ExceptionTrace` therefore takes fewer site samples when one is
  attached: 5 per second rather than 20.

---

## Adding to it

**A new record kind:** add it to `ScopeKind` in `RPM.Core/Diagnostics/ScopeRecord.cs` with a permanent
number (never renumber, because a reader attached to a long run resolves old records), mirror it in
`tools/rpmscope/rpmscope/ring.py`, and add a `Lane` in `viewer.py` if it deserves a row.

**A new trace channel:** call `Said.Line("yourchannel", text)`. The window grows a chip for it by
itself, and `log --channel yourchannel` filters to it.

**The record layout is shared between two languages and no compiler checks the agreement.** That
agreement is a pair of tests: `ScopeFormatTests` writes a fixture with every field set to a number
that could not be arrived at by accident, and `tests/test_ring.py` asserts the same numbers come back.

```sh
dotnet test RPM.Core.Tests/RPM.Core.Tests.csproj --filter FullyQualifiedName~ScopeFormatTests
cd tools/rpmscope && ./.venv/bin/python -m pytest tests/ -q
```

---

## Why it exists

Some stalls cannot be explained from inside the stalled thread. One example is a three-way convoy
between the drawing thread, the debugger's own thread and a collection. It held the app for 13.5
seconds, and 83% of that was the drawing thread inside a single exception send. rpmscope reads the
rings from another process so that such a stall can still be explained.

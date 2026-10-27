# g0-host recon: the launcher and the lag meter

Read-only. Nothing built or run, so every "does" below is from reading source. Paths: `W` = `~/dev/work/worktrees/Assembly/g0-host`,
`R` = `~/.cargo/registry/src/index.crates.io-1949cf8c6b5b557f`, `K` = `R/gpui-pre-0.3.10/src`. Note: the stubs in
`W/desktop/src/core/backend/` and `W/desktop/src/features/meter/` were being edited by someone else while I read.

## A. The launcher

### Facts about the backend

- Command: `Assembly.Desktop serve [--port N] [--home DIR] [--token-file FILE] [--agents FILE] [--remote H] [--remote-port N]
  [--remote-token-file P]`; pairs only, anything else prints usage and exits 2 (`W/dotnet/Assembly.Desktop/Serve/DesktopOptions.cs:17,35-52`,
  `Program.cs:3-13`). Defaults: port 18797, home `~/assembly-dotnet/mac`, token `~/.config/assembly-dotnet/env`, agents
  `HOME/agents.json` (`DesktopOptions.cs:25-28,52`).
- The agents file must already exist: `File.ReadAllText(options.AgentsFile)` throws if not, so the child dies before it is ready
  (`DesktopServe.cs:22`). Today `shell/assembly.sh start` writes it with `dotnet/scripts/agents-file.sh` first (`shell/assembly.sh:44`).
- Token file: `TokenFile.ReadOrCreate` reads the first `ASSEMBLY_TOKEN=<hex>` line, or makes 32 random bytes as hex, creates the
  folder, writes the file and sets mode 0600 on first start (`W/dotnet/Assembly.Infrastructure/Http/TokenFile.cs:12-27`). It runs
  before the server starts (`DesktopServe.cs:21`), so the file exists when the ready line comes.
- What it prints: remote tunnel messages and recovery messages first (`DesktopServe.cs:23`, `DesktopBackend.cs:73`), then, once the
  server is up, three lines: `listening on {backend.Address}`, `home:  {path}`, `token: in {tokenFile}` (`DesktopServe.cs:26-28`).
  `Address` is a `Uri`, so the line reads `listening on http://127.0.0.1:18797/` (trailing slash; `DesktopBackend.cs:51,55`). The
  `token:` line prints the file's path, not the token. Later it prints `page: {method}` per page call (`PageRoutes.cs:27`). The only
  `listening on` text in the repo besides `Assembly.Remote` (`ServeRun.cs:53`, not used here).
- Shutdown: `Program` has no signal or stdin code. The host (`WebApplication.CreateSlimBuilder`, `ApiServer.cs:26`) stops on SIGTERM
  or Ctrl+C through .NET's default console lifetime (assumed from .NET defaults; the old shell relies on it, `shell/src/main.rs:85`).
  Closing stdin does nothing (grep for stdin reads: only `AgentProcess.cs`). Stop is bounded at 8 s by `ShutdownTimeout`
  (`ApiServer.cs:22,28`), but `DisposeAsync` (stop sessions, tunnel) runs after `WaitForShutdownAsync` and has no bound
  (`DesktopServe.cs:24-30`, `DesktopBackend.cs:109-125`). It exits 0 after a stop.
- Kill it by pid, not by stdin: if the window is SIGKILLed or crashes, nothing stops the child.

### How the old window starts it today (`W/shell/src/main.rs`, Tauri, not the old mac-command-bar)

`~/dev/work/mac-command-bar` has no .NET backend launcher: grepping `tauri-svelte-preview/src-tauri/src` for `Command::new`,
`sidecar`, `kill`, `RunEvent` finds only git, `security` and reaper test code. The real predecessor is this repo's `shell/`:
- Reads `backend/lane.json` (port, home, tokenFile, remotePort, remoteTokenFile) (`main.rs:28-38`), refuses if the port is already
  open (`:46`), appends stdout and stderr to `HOME/backend.log` (`:51,58`), adds `--remote ...` when `ASSEMBLY_REMOTE` is set (`:55`).
- Waits for the port by polling every 100 ms up to 20 s (`:60-66`) and reads the token from the file afterwards (`:71-77`, `:108`).
  Neither the poll nor the 20 s may be copied: the guard bans `sleep`, `TcpStream` (`desktop/scripts/guard.sh:17,22,39`).
- On quit: SIGTERM, poll for exit up to `BACKEND_STOP_WAIT` = 10 s, then SIGKILL (`main.rs:81-95`, `:122-126`). No restart; the shell
  has no restart today (plan 5.10 says restart comes with the reconnect group, `implementation-plan.md:720-738`).
- Where the values come from: `scripts/lane-env.sh` sets plain shell variables (not exported): `DESKTOP_PORT=18797+10*LANE_NUMBER`,
  `DATA=~/assembly-dotnet-<lane>`, `TOKEN_FILE=~/.config/assembly-dotnet-<lane>/env`, `BOX_PORT`, `BOX_TOKEN_FILE`
  (`scripts/lane-env.sh:19-25`). g0-host is lane 8 (`W/.lane`), so port 18877. `BackendConfig::from_env` reads "the environment
  lane-env.sh sets" (`backend_config.rs:1-3`), but nothing exports these; a script must `export` them or pass them another way.
- Build output: `shell/assembly.sh build` publishes AOT osx-arm64 to `shell/backend/Assembly.Desktop` (`shell/assembly.sh:29`);
  `dotnet/scripts/desktop.sh serve` publishes JIT to `~/assembly-dotnet/mac/bin/<commit>/Assembly.Desktop` or AOT with `AOT=1`
  (`dotnet/scripts/desktop.sh:62,65`). So `program` is a single path or `dotnet <dll>` (the stub already allows both, `backend_config.rs:8-9`).

### Kit facts

- Run on quit: `App::on_app_quit(|cx| async {...}) -> Subscription` (`K/app.rs:2851-2867`). `shutdown()` calls every observer
  synchronously (`app.rs:1233-1235`), clears windows, then polls the returned futures with a hard 200 ms limit
  (`app.rs:79,1252-1262`, then logs "timed out waiting on app_will_quit"). Quit via last window closed calls `cx.quit()` (`app.rs:3317-3323`)
  which reaches this path (`app.rs:1149-1161`). Quit requested while the app cell is borrowed returns false and is the platform's
  problem (`app.rs:1162-1166`). Keep the `Subscription` alive (store it in the Launcher), or the hook is dropped.
- Consequence: a blocking call placed in the closure body, before the future is returned, is not under the 200 ms limit
  (it runs at `app.rs:1234`); only the returned future is. SIGTERM then wait for the exit, up to the backend's 8 s, would freeze
  the window for that time with the windows still on screen. The 10 s kill fallback of the old shell needs a clock; see Open question 3.
- Background thread to main without polling: the kit does it itself. `App::spawn(async |cx| { while let Ok(x) = receiver.recv().await {
  cx.update(|cx| ...) } })` (`K/app.rs:2494-2503`), with `AsyncApp::update` (`K/app/async_context.rs:176`). The receiver there is
  `async_channel` (`K/test.rs:163`, `hot_reload.rs:36`). `async-channel 2.5.0` is already in `desktop/Cargo.lock:300` (not a direct
  dependency); `futures-channel 0.3.34` is `Cargo.lock:1755`, and g0-socket added it as a direct dependency
  (`g0-socket/desktop/Cargo.toml`, `=0.3.34`). `AsyncApp` is not `Send`, so the reader thread sends and a main-thread task receives.
  `async_channel::Sender::send_blocking` fits a plain thread. `futures-channel` needs a stream trait to `.next()`; async-channel needs none.
- `libc 0.2.191` is in the lock (`Cargo.lock:3368`) but not a direct dependency of `desktop/`. `libc::kill(pid, SIGTERM)` as in the
  old shell (`main.rs:85`) needs it added: one line, exact pin.

### Proposed flow (smallest that meets the brief)

1. `BackendConfig::from_env` (stub exists). `Launcher::start` spawns the child with stdout piped, stderr merged into the same log, and
   starts one `std::thread` that owns reading. No timers.
2. Thread: read lines until EOF. Every line goes to the window's stdout unchanged (so `listening on` is visible), except nothing
   is parsed beyond a `listening on ` prefix. On that line: read `token_file` (first `ASSEMBLY_TOKEN=` line, same rule as
   `TokenFile.cs:16`), build `BackendReady { address, token }` (no Debug, per the stub), send it on the channel, set `ready = true`.
3. At EOF, `child.wait()`. If `quitting` is set: stop. Else if `ready`: start a new child (back to step 1, `ready = false`). Else: send
   a "backend did not start, exit status N" report and stop.
4. Main-thread task (`cx.spawn`): `while let Ok(ready) = rx.recv().await { socket.update(cx, |s, _| s.reconnect()) }`.
   `SocketClient::reconnect(&self)` is declared in `g0-socket/desktop/src/core/socket/client.rs:66` (still `todo!()`), and
   `Endpoint { port, token }` in `socket/endpoint.rs`. The port comes from parsing the `listening on` address; the launcher must
   hand over `Endpoint` first, or `reconnect` has no way to learn a restarted child's token/port (open question 1).
5. `cx.on_app_quit`: set `quitting`, `libc::kill(pid, SIGTERM)`. The stub's doc comment says it waits; see Risks.

## B. The meter

### What the spike did (`~/dev/work/assembly-gpui-spike/app/src/meter.rs`)

Turned on by `ASSEMBLY_METER=<csv>` (`config.rs:6,33`); `set_trace_enabled(true)` (`meter.rs:30`); keys by
`cx.intercept_keystrokes` pushing `Instant::now()` (`:34`); stalls by a 100 ms `background_executor().timer` loop, "late" = extra
elapsed time (`:37-45`); a thread that sleeps 5 s, then drains `FrameTimingCollector::collect_unseen()` (`:47,57,63,71`). CSV columns
(`:52`): `at,frames,lag_p50_ms,lag_max_ms,lag_50,lag_100,draw_max_ms,keys,key_p50_ms,key_max_ms,stall_max_ms,stalls_50,run_lag_max_ms,run_key_max_ms`.
It has no memory column; memory was read from GPUI's HUD with the meter off because the trace keeps up to 16 MB (`app/README.md`
"Memory is measured with the meter off"; cap at `K/profiler.rs:1577-1578`).

### GPUI's trace API (all behind the `profiler` feature, which `gpui-fps` switches on: `R/gpui-fps-0.8.1/Cargo.toml:46-48`; `desktop/Cargo.toml` already depends on `gpui-fps`)

- `set_trace_enabled(bool) -> bool` (`K/profiler.rs:707`), `trace_enabled()` (`:765`). Off clears the buffers (`:728-731`).
  `record_frame_event` does nothing while off (`:1598-1602`), so the collector sees nothing and costs nothing at rest.
- `FrameTimingCollector::new()` / `collect_unseen() -> Vec<FrameEvent>` (`:1627-1650`), cursor based, ring 16 MB, wrapped entries lost.
  `FrameEvent::{Draw(FrameTiming), Present(PresentTiming)}` (`:972-979`). `FrameTiming`: `dirty_at: Option<Instant>`,
  `draw_start`, `draw_end`, `signal_at: Option<Instant>`, `refresh_interval`, `phases` (`:788-811`); helpers `work_start()` (later of
  signal and dirty, `:889-899`), `draw_duration()` (`:934`). `PresentTiming.present_start/present_end` (`:949-960`).
  `Instant` here is `web_time::Instant`, std's on native (`gpui-pre-scheduler-0.3.10/src/clock.rs:5`).
- Is collecting without frame callbacks enough? For frame lag and draw time, yes: a frame is recorded when GPUI draws and presents
  it, and the meter asks for none (the spike's note and `profiler.rs:1554-1562`). For stalls, only partly: a stall that dirties
  no frame leaves nothing in `FrameEvent`s. Without a timer you can still see a stall when a dirtied frame waits: `draw_start - work_start()`
  (needs `signal_at`, which I could not confirm macOS fills; `K/platform.rs:1052`, set `K/window.rs:1787,1904`), or `draw_start - dirty_at`
  (includes up to one refresh of normal waiting, fine against a 50 ms threshold).
- Better stall source, also no timer: the foreground journal. `App::foreground_journal()` (`K/app.rs:2385`) gives a handle;
  `.collector()` (`K/profiler/journal.rs:1320`) and `collect_unseen() -> DrainedEntries { entries, lost }` (`:1353`) return
  `ForegroundJournalEntry::Event(ForegroundEvent::{TaskPoll, Action, Input, Draw, Present, ...})` (`:78-95,289-300`), each with
  start and end times. The journal is installed with the app (`app.rs:968`) and not gated on `set_trace_enabled`
  (no `trace_enabled` call in `journal.rs`). The longest event duration in an interval is "how long the main thread was busy with
  one thing", which is what the spike's late timer measured, and it also sees stalls with no frame. TaskPolls under 100 microseconds
  are folded (`journal.rs:39`). This is the same data GPUI's own hang monitor reads (`K/app.rs:2408`, `profiler/hang.rs`).
- Key to screen: the spike's way (own `intercept_keystrokes` time, then the first presented frame whose `draw_start` is not before
  the key) keeps the numbers comparable (`meter.rs:79-86`). The journal has it too: `Input { kind: "key_down", start, caused_invalidation }`
  (`journal.rs:134-145`, kind names `K/interactive.rs:871`) and `IntervalBoundary::Presented(PresentedFrame { frame, presentation })`
  (`journal.rs:210-217,228-232`). The spike's way counts keys that dirty nothing and waits for the next unrelated frame (inflating key max);
  the journal's `caused_invalidation` avoids that. Also `Window::input_latency_snapshot()` (`K/window.rs:3649`) gives an always-on
  histogram of input to frame, but cumulative and for every input kind, so not comparable.
- Dirty time: `dirty_at` is set at the first invalidation (`K/window.rs:261`); the spike falls back to `draw_start` when it is `None`
  (`meter.rs:76`), which happens for a frame dirtied before the trace was on, so call `set_trace_enabled(true)` before opening the window
  (as the spike did, `main.rs:63-64`).

### Footprint

- macOS: `proc_pid_rusage(getpid(), RUSAGE_INFO_V2, &mut rusage_info_v2)` then `.ri_phys_footprint` bytes, which is what Activity Monitor
  shows. GPUI's HUD already does exactly this (`R/gpui-fps-0.8.1/src/memory/macos.rs:17-58`), but its `memory` module is private
  (`gpui-fps/src/lib.rs:43`, only `FpsMonitor` and `FpsOverlay` are exported, `:50-51`), so g0-host copies the 15 lines. `libc` has
  `proc_pid_rusage` (`R/libc-0.2.191/src/unix/bsd/apple/mod.rs:5511`), `RUSAGE_INFO_V2` (`:4251`) and `rusage_info_v2` (`:1126`). `mach2 0.6.0` is in
  the lock (`Cargo.lock:3563`) but pulled by GPUI; `task_info` is not needed.
- Linux: `/proc/self/status`, the `RssAnon:` line in kB times 1024 (`gpui-fps/src/memory/linux.rs:25,47-54`). `VmRSS` also exists but
  counts shared library pages; `RssAnon` matches what the HUD shows. A file read in `footprint.rs` is allowed: the guard only bans file reads
  in files that draw (`guard.sh:24-25,40`).
- Not the backend's footprint; the old page showed app, backend and WebContent RSS from a button (`app/src/routes/+page.svelte:88-89`,
  `MemorySample.cs`, `DiagnosticsFeature.cs:24`). The new column is the window's process only.

### The backend clock topic (what the meter hangs on)

Topic `clock`, key `second`; the payload is `{ "now": <Unix ms> }` (`ClockTime.cs:4`, camelCase `ClockJson.cs`). Subscribing answers at
once with one state frame (`BackendClock.cs:31-38`), then a `change` each second from a `PeriodicTimer` while at least one socket
subscribes; the timer stops itself at the first tick that finds no subscriber (`:53-70`), so subscribe only while the meter is on
and unsubscribe when off. The first frame is not a second tick, so "every fifth tick" is five changes after the state frame, or
better, write a line when `now` has advanced 5 s. g0-socket's `BackendClock` entity (`g0-socket/desktop/src/core/clock/backend_clock.rs`)
has `hold(cx)` / `release(cx)` and emits `Tick { now: i64 }`, still `todo!()`; the meter should use exactly those two calls.

### "The soak switch"

I found no phrase "soak switch" anywhere (`notes/`, `docs/`, plan). Candidates, closest first: (1) the spike's `ASSEMBLY_BENCH_SCROLL=<s>`
(`config.rs:7,34`), scroll up one item per frame for that many seconds, which is how the spike baseline was driven; (2) the old page's
`?soak=1|light|switch|scroll|seed` modes (`+page.svelte:98-104`, `app/src/lib/soak.ts:1-25`), passed by `ASSEMBLY_QUERY`
(`shell/src/main.rs:110`); (3) `shell/assembly.sh start --counters` (`:46`) which sets `?counters=1` and starts the lag meter plus a CSV
every 2 s (`+page.svelte:136-141`, `soak.ts:481-495`). The behavior list only requires `ASSEMBLY_METER=<csv>` on or off
(`docs/behavior/g0-host.md` #14-15; stub `meter/mod.rs:1`). Plan D2 moves the soak out of the app into a script (`implementation-plan.md:81-91`),
so I treat it as not part of this lane.

### Column mapping

| Old page CSV (`soak.ts:485`) | Spike CSV | Same thing? |
|---|---|---|
| `jank_50`, `jank_100` (frame gaps over 50/100 ms, running total, `lag.svelte.ts:25-26`) | `lag_50`, `lag_100` (frames whose dirty-to-screen passed 50/100 ms, per 5 s) | Different: gap between animation frames vs lag of a drawn frame; running vs per interval |
| `worst_5s` (largest gap in the last 5 s) | `lag_max_ms` | Close in spirit only |
| `worst_run` | `run_lag_max_ms` | Same idea, different measure |
| `key_worst`, `key_median` (last 20 keys, keydown to the frame after the handling one, `lag.svelte.ts:48-56`) | `key_max_ms`, `key_p50_ms` (keys in the interval) | Close |
| none | `frames`, `lag_p50_ms`, `draw_max_ms`, `keys`, `stall_max_ms`, `stalls_50` | spike only |
| `memory.sample` button | none | new `footprint_mb` |

Numbers compare with the spike, not with the old page. The stub's header puts `footprint_mb` before `run_lag_max_ms`
(`meter_line.rs:34`), so a reader that picks columns by position loses the last two; appending it last keeps all 14 spike columns where they were.

### Proposed flow

`ASSEMBLY_METER` unset: `Meter::start` is never called; no trace, no subscription, no keystroke interceptor, no file. Set: before the
window opens, `set_trace_enabled(true)`, create the collector (and journal collector), `intercept_keystrokes` into a `Vec<Instant>`,
`BackendClock::hold`. Each `Tick` event on the main thread: when 5 s have passed, drain, build one `MeterLine` (pure function over
frames and key times), read `footprint_mb()`, append the line and print it. The CSV file stays open or is reopened per line; no thread.
Stopping (not required by the brief) is `release`, dropping the subscription, `set_trace_enabled(false)`.

## Proposed tests (behavior only)

Launcher, with a fake program (`/bin/sh -c`) in place of the backend; avoid the word `sleep` in test strings, the guard scans them
(`guard.sh:22,39` match `sleep` in any non-comment line): use `read` on a held-open stdin, or `exec tail -f /dev/null`.
1. The ready line makes one `BackendReady` with its address and the file's token; other lines do not.
2. A ready child that exits is replaced at once and the new child's ready line signals again.
3. A child that exits before its ready line is not replaced; a failure is reported once.
4. Quit sends SIGTERM to the live child, starts no other, and the child is gone afterwards (check by pid).
5. The token never appears in what the window prints or logs: the fake prints a line containing the token's path, the file holds a
   unique token, and the test scans the captured output, the report and `Debug` text for it.
6. A garbled or missing token file does not signal ready and does not restart (report names the file, not its contents).

Meter, with no window:
7. `MeterLine::from_frames` on a hand-built list of `FrameEvent`s gives exact frames, lag p50/max, 50/100 counts, draw max.
8. Keys: a key before a frame's `draw_start` is cleared with its key-to-screen time; a later key stays waiting.
9. Stall max and count over 50 ms from a recorded journal or frame list.
10. The CSV line has exactly the header's columns, in order, `at` first, worst-so-far columns carry across lines.
11. Footprint: the Linux reader returns a positive number and rises by about the size of an allocated and touched buffer (as `memory/linux.rs:56+` tests).
12. Off: with no `ASSEMBLY_METER`, `set_trace_enabled` stays false, `FrameTimingCollector` returns nothing, no CSV file exists.
13. `FrameEvent`s are constructed by tests: `FrameTiming` has public fields, but needs `window_id: WindowId`, which I have not shown can be built outside GPUI.

## Risks

- A blocking SIGTERM wait inside `on_app_quit` freezes the window up to 8 s; an async wait is cut off at 200 ms (`app.rs:1252-1262`).
  Without a wait, the app exits while the backend still drains writes, and a quick relaunch finds the port taken (the child exits
  before ready, so it is not restarted).
- The 10 s SIGKILL fallback needs a clock; only `core/clock/` may wait on time (`guard.sh:12,36`), and it holds exactly two things.
- An app crash or SIGKILL leaves the backend running with nothing to stop it. The next start fails to bind and is reported, not retried.
- A backend that becomes ready then crashes at once, forever, restarts forever (as specified; each cycle costs a startup).
- The agents file must be written before launch, and `lane-env.sh` values are not exported. If the app starts only the binary, these
  two make a silent exit-before-ready.
- EOF on stdout is the exit signal. A grandchild keeping the pipe open (an adapter does not: it redirects its own output,
  `AgentProcess.cs:34-36`) would delay it; use a separate `child.wait()` thread if that ever shows up.
- The reconnect hand-off depends on g0-socket's still-unwritten `SocketClient::reconnect` and `BackendClock::hold/release`.
- The meter is blind while the socket is down: no ticks, no lines, and the trace ring wraps at 16 MB (`profiler.rs:1577`).
- Spike and new numbers differ if the stall source differs (journal longest event vs the spike's 100 ms late timer) or if key
  times use `caused_invalidation`; say so in the first CSV's header comment or README.
- `signal_at` may be `None` on macOS (not verifiable on this Linux machine; the macOS platform crate is not in the registry).

## Open questions for Jesse

1. On a restart the new child may have a new port or token. Should `reconnect` take the new `Endpoint`, or is the port always fixed
   (18797 + 10 * lane) and the token file stable? I recommend a fixed port and passing `Endpoint` anyway at first start.
2. Is it right that a crashed window leaves the backend running? A fix needs a .NET change (exit when stdin closes) outside this lane.
3. At quit, which do you prefer: SIGTERM and exit at once (backend finishes on its own, up to 8 s), or SIGTERM and freeze up to 8 s
   until it stops, with the 10 s SIGKILL fallback living in `core/clock/`?
4. Stalls: use GPUI's foreground journal (also sees stalls with no frame, closest to the spike) or only frame timings (skeleton text)?
5. Key-to-screen: the spike's own key times (comparable, can inflate) or the journal's input events that invalidated?
6. Put `footprint_mb` last (keeps the spike's columns in place) or before the `run_*` columns as in the stub?
7. What is "the soak switch"? `ASSEMBLY_BENCH_SCROLL`, the old `?soak=` modes, `--counters`, or something in notes I cannot see?
8. Should the window write `agents.json` (`agents-file.sh` needs node and pnpm) or does a script do it before launch?

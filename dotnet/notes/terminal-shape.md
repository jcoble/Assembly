# Terminal host: shape notes

Step W4 (Terminals) of `docs/plans/2026-10-26/implementation-plan.md`, from Sol's P22. Behavior list:
`docs/behavior/terminal.md` (draft). Step 1 (files and red tests) is 523f338; step 2 fills them in, with the
changes the pre-implementation check asked for (listed at the end).

## What the backend does, and what it doesn't

The backend starts each terminal's process on a pseudo-terminal (PTY), keeps its recent output, and passes bytes both
ways. It never parses the output: no VT parser, no headless emulator, no UTF-8 decoding. Ghostty, in the window, is
the only thing that reads escape sequences. The old app decoded every 8 KB read with `from_utf8_lossy`
(`terminal/mod.rs:510`), which broke a character split across two reads; keeping bytes fixes that (terminal#3).

## Files

```text
Assembly.Application/Features/Terminal/
  TerminalFeature.cs     the calls and the two topics
  TerminalHost.cs        this machine's terminals, by id: start, write, resize, read, close, close all
  RunningTerminal.cs     one terminal: its PTY, its output ring, its exit; publishes its output as it reads it
  OutputRing.cs          the kept output: bytes with absolute offsets, 16 MB cap, trims to 12 MB
  IPtyStarter.cs         starts a process on a new PTY (implemented in Infrastructure)
  IPty.cs                one started PTY: read, write, resize, exit, dispose ends the process group
  PtyRequest.cs
  TerminalInfo.cs  StartArgs.cs  WriteArgs.cs  ResizeArgs.cs  TerminalKey.cs  ReadArgs.cs  OutputPiece.cs  OutputRow.cs  Empty.cs
  TerminalJson.cs        source-generated JSON, for AOT
Assembly.Infrastructure/Terminal/
  UnixPtyStarter.cs      posix_openpt, then the child through /usr/bin/perl (see below)
  UnixPty.cs             the master side: a FileStream over the master fd, TIOCSWINSZ, the process group kill
  PtyNative.cs           the libc imports (DllImport with blittable arguments, as ProcessGroup.cs, so it works under Native AOT)
```

## The PTY boundary (P22's dependency proof)

One approach, no new package and no native helper to ship:

1. The backend opens the master with `posix_openpt(O_RDWR | O_NOCTTY)` (with `O_CLOEXEC` on Linux, `FIOCLEX` after on
   macOS), then `grantpt`, `unlockpt`, `ptsname`, sets the size with `TIOCSWINSZ`, and opens the slave itself
   (close-on-exec), holding it until the process ends, so the master can't read end of file before perl opens it.
2. The child goes through `/usr/bin/perl`, as `Agents/ProcessGroup.cs` already does for `setpgrp`: perl calls
   `setsid`, opens the slave by name, makes it the controlling terminal (`TIOCSCTTY`, whose number the backend passes
   in for the platform), puts it on fds 0, 1 and 2, and execs the shell. The pid stays the same, and
   the shell leads its own session and process group, so job control and Ctrl-C work (terminal#2). Closing uses
   `ProcessGroup.Kill`, which also kills the groups of the jobs an interactive shell started (terminal#8).
3. Resize is `TIOCSWINSZ` on the master. `ioctl` is variadic, and on Apple arm64 a variadic argument goes on the
   stack, so there `PtyNative` passes six unused register arguments first. Exit is `Process.ExitCode`; a signal
   shows as 128 + n, as .NET reports it, so the exit is one code.
4. A thread of its own reads the master (a read blocks for the terminal's life) into a 4-slot channel, so a slow
   reader holds the program up, not memory. Linux answers EIO once the last slave closes; that's end of output. The
   thread owns the master and closes it when done, never under a blocked read.

Forking from managed code isn't safe, and `posix_spawn` can't set a controlling terminal on macOS, which is why the
child step is perl. The proof is `Draft/Terminal/UnixPtyTests.cs` passing on Linux (agent-workbox, Workbox Test) and
macOS (arm64, which runs the padded `ioctl`), and in the desktop's Native AOT build, made on the Mac by
`shell/assembly.sh`. Workbox Test runs a normal build. Linux passes; the Mac and AOT runs are still to do.

## The wire

All on the one socket, as JSON frames. Byte fields (`data`) are `byte[]` in C#, which System.Text.Json writes as
base64: bytes in, bytes out, base64 only as the frame's encoding. Jesse's D4 decision (one frame format, about 1 MB a
call) holds, so there's no binary frame.

| Call | Args | Answers |
| --- | --- | --- |
| `terminal/start` | `cwd`, `shell?`, `command?`, `cols?`, `rows?`, `owner?` | `TerminalInfo` |
| `terminal/list` | none | `TerminalInfo[]`, each with `exitCode` once it has ended |
| `terminal/write` | `terminal`, `data` | `{}` |
| `terminal/resize` | `terminal`, `cols`, `rows` | `{}` |
| `terminal/read` | `terminal`, `offset` | `OutputPiece`: `offset`, `end`, `data` (at most 1 MB) |
| `terminal/close` | `terminal` | `{}`; ok for one already closed |

The topic, `terminal`, keyed by terminal id, a rows topic (`ICallRegistry.Rows`): each read from the PTY goes out as one row,
  `{"seq": <end offset>, "prevSeq": <start offset>, "data": <bytes>}`. Offsets count bytes since the terminal
  started. The sub's `after` is the last offset the window has; the state catches up from it in one row when it's in
  the ring and at most 1 MB behind, otherwise it answers the current end and the window reads the replay with
  `terminal/read`, 1 MB a call, from its `after` but no earlier than 4 MB before that end (terminal#5). Reads
  themselves only raise an offset to the oldest kept byte, so output arriving between pages never skips any. The state never holds more than
  1 MB, since it runs under the Topics lock.

The exit is the last row, `{"seq": <end + 1>, "prevSeq": <end>, "exit": <code>}`, published only after the PTY has
read end of file and the process has ended, so the last output always goes first (terminal#7). A state for an exited
terminal ends with it too.

Each read is appended under the terminal's lock and published after it lets go: the state runs under the Topics lock
and then takes the terminal's, so publishing under it would take them the other way round. A row the state already
holds is dropped by its seq (`SocketDispatcher.Rows`).

Calls that aren't database writes are reads, and reads run side by side, so two writes could reach the PTY in the
wrong order. The window keeps one `terminal/write` in flight per terminal and sends what's typed meanwhile in the
next one.

The folder check (terminal#1) is in `UnixPtyStarter`, since the core opens no files (`ArchitectureTests`).

## Ownership and the way out

- Terminals outlive sockets: a closed socket only drops its subscriptions (terminal#5).
- `terminal/close` disposes the PTY (SIGKILL to its process group, then closes the master) and drops the ring.
- `TerminalHost` is disposed when the host stops, and closes every terminal (terminal#8).
- `owner` is the chat session that asked; a start with an owner whose terminal is still running answers that one
  (terminal#9). It's passed to the shell as `ASSEMBLY_SESSION_ID`, as the old app passed `COMMANDBAR_SESSION_ID`.

## Environment and arguments

`TERM=xterm-256color`, `COLORTERM=truecolor`, `TERM_PROGRAM=Assembly`, and `NO_COLOR` removed, as the old app did.
Also `CLICOLOR`, `CLICOLOR_FORCE`, `FORCE_COLOR=3` and `COLORFGBG=15;0`, as the old app set. The shell is `shell`,
else `$SHELL`, else `/bin/sh`, always started with `-l` so the terminal has the user's full PATH, plus
`-c <command>` when there's a command. A blank command is no command. The old app gave `-l` only to a list of shell
names; with no lists, every shell gets it, and a shell that refuses `-l` fails to start.

## What waits for later steps

- Remote (terminal#10): `TerminalFeature` is in the socket features of both hosts (`DesktopBackend.cs` and
  `ServeRun.cs`), and each host disposes its `TerminalHost` when it stops. Until C2 it's proved against the Workbox
  Test host's own socket. Reaching it from the desktop's window comes with C2's socket relay, which forwards a machine's calls
  and topics; nothing terminal-specific is needed there.
- Agent tool terminals (ACP `terminal/*`) stay separate, as P22 says.
- Closing a session's terminals when the session is deleted comes with that delete.
- The window side (the Ghostty view and its socket calls) is the GPUI lane's.

## Known risk: a flood of output

Every PTY read is a row, and a socket closes past 16 MB queued (`SocketConnection.Send`). A `yes` flood to a slow
or remote window could close the socket, chat included, again on each reconnect. The 4-slot channel slows the
program only as fast as the backend publishes, not as fast as the window reads. Next in this lane: a socket-level
flood test; if it closes the socket, rows carry only offsets and the window pulls bytes with `terminal/read`.
`notes/terminal-later-backend.md` (outside the repo) maps the options.

## Known limit: a job left running

The exit row waits until nothing holds the terminal. Under bash, `sleep 1000 &` then `exit` leaves the job holding
it, since bash doesn't hang up its jobs, so the window shows the shell as running until the job ends (zsh hangs
them up). Closing the terminal kills that job's group too.

## Open questions for Jesse

- `ASSEMBLY_SESSION_ID` as the owner variable's name.
- `/bin/sh` as the last fallback shell (the old app used `/bin/zsh`).
- Whether the exit should go out when the shell ends, even while a job it left holds the terminal.
- `-l` for every shell (no list of shell names), so an unusual shell that refuses it won't start.

## What the pre-implementation check changed

1. Lock order: publish after letting go of the terminal's lock (it was under it).
2. No `terminals` list topic: a keyless topic can't remove yet (`SocketDispatcher.ChangeFrame` only upserts), so the
   list is a call and the exit is the output topic's last row.
3. One write in flight per terminal, in the wire contract.
4. The folder check moved to `UnixPtyStarter`; its test to `UnixPtyTests`.
5. Wired into `DesktopBackend.cs` and `ServeRun.cs`, not `MachineFeatureList` (HTTP only).
6. PTY: EIO is end of output, a reader thread of its own, the master closed by that thread, the parent holds the
   slave until exit, `ProcessGroup.Kill`, the group test runs an interactive shell, DllImport (no
   `AllowUnsafeBlocks`), `ptsname` copied under a lock, Apple arm64 `ioctl`.
7. Floods: recorded above as the next step, not fixed yet.

## What the code reviews changed

- Claude: no group kill once the program has ended and nothing holds the terminal (its pid may be someone else's);
  the master is a SafeFileHandle, so a write or resize never reaches a reused number; the PTY starts outside the
  host's lock; a window that has the exit gets nothing again; only a session's shell is reused, never a command it
  runs (`TerminalInfo.Command`).
- Sol 6.1: closing also kills every process left in the terminal's session (`getsid`), so a job an interactive shell
  left when it exited dies too; replay pages no longer reapply the moving 4 MB floor; a start that was running when
  the host stopped disposes what it opened and fails; a cancelled or timed-out write answers at once (its thread
  stays in `write()` until the program reads or the terminal closes). Its stale-resize and repeated-exit findings
  were the same as Claude's, already fixed.

## Later steps

`terminal-later-window.md` (the Ghostty client, the old terminal UI, Run, sign-in) and `terminal-later-backend.md`
(remote through C2, floods, interop, the Mac check list), next to this file, map what comes after this step.

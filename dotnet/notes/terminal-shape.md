# Terminal host: shape notes

Step W4 (Terminals) of `docs/plans/2026-10-26/implementation-plan.md`, from Sol's P22. Behavior list:
`docs/behavior/terminal.md` (draft). This is step 1 of "files first, then code": every file exists with its
signatures and comment, and the draft tests in `Assembly.Tests/Draft/Terminal/` fail until step 2 fills them in.

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
  PtyRequest.cs  PtyExit.cs
  TerminalInfo.cs  StartArgs.cs  WriteArgs.cs  ResizeArgs.cs  TerminalKey.cs  ReadArgs.cs  OutputPiece.cs
  TerminalJson.cs        source-generated JSON, for AOT
Assembly.Infrastructure/Terminal/
  UnixPtyStarter.cs      posix_openpt, then the child through /usr/bin/perl (see below)
  UnixPty.cs             the master side: a FileStream over the master fd, TIOCSWINSZ, the process group kill
  PtyNative.cs           the libc imports (LibraryImport, so it works under Native AOT)
```

## The PTY boundary (P22's dependency proof)

One approach, no new package and no native helper to ship:

1. The backend opens the master with `posix_openpt(O_RDWR | O_NOCTTY)`, then `grantpt`, `unlockpt`, `ptsname`, and
   marks it close-on-exec, so no other child inherits it. All of these are libc on macOS and Linux.
2. The child goes through `/usr/bin/perl`, as `Agents/ProcessGroup.cs` already does for `setpgrp`: perl calls
   `setsid`, opens the slave by name, makes it the controlling terminal (`TIOCSCTTY`, whose number the backend passes
   in for the platform), puts it on fds 0, 1 and 2, sets the size, and execs the shell. The pid stays the same, and
   the shell leads its own session and process group, so job control and Ctrl-C work (terminal#2) and closing kills
   the group (terminal#8).
3. Resize is `TIOCSWINSZ` on the master. Exit comes from `Process.WaitForExitAsync`; a signal shows as 128 + n, which
   `PtyExit` splits back out.

Forking from managed code isn't safe, and `posix_spawn` can't set a controlling terminal on macOS, which is why the
child step is perl. The proof is `Draft/Terminal/UnixPtyTests.cs` passing on Linux (agent-workbox, Workbox Test) and
macOS, and in the Native AOT build of `Assembly.Remote`.

## The wire

All on the one socket, as JSON frames. Byte fields (`data`) are `byte[]` in C#, which System.Text.Json writes as
base64: bytes in, bytes out, base64 only as the frame's encoding. Jesse's D4 decision (one frame format, about 1 MB a
call) holds, so there's no binary frame.

| Call | Args | Answers |
| --- | --- | --- |
| `terminal/start` | `cwd`, `shell?`, `command?`, `cols?`, `rows?`, `owner?` | `TerminalInfo` |
| `terminal/write` | `terminal`, `data` | `{}` |
| `terminal/resize` | `terminal`, `cols`, `rows` | `{}` |
| `terminal/read` | `terminal`, `offset` | `OutputPiece`: `offset`, `end`, `data` (at most 1 MB) |
| `terminal/close` | `terminal` | `{}`; ok for one already closed |

Topics:

- `terminals`, no key: every terminal on this machine as `TerminalInfo`, upserted on start and on exit (with its exit
  code or signal), removed on close. That's how the window sees which state each one is in (terminal#7).
- `terminal`, keyed by terminal id, a rows topic (`ICallRegistry.Rows`): each read from the PTY goes out as one row,
  `{"seq": <end offset>, "prevSeq": <start offset>, "data": <bytes>}`. Offsets count bytes since the terminal
  started. The sub's `after` is the last offset the window has; the state catches up from it in one row when it's in
  the ring and at most 1 MB behind, otherwise it answers the current end and the window reads the replay with
  `terminal/read`, 1 MB a call, starting no earlier than the last 4 MB (terminal#5). The state never holds more than
  1 MB, since it runs under the Topics lock.

The exit is published on `terminals` only after the PTY reader has hit end of file, so the last output always goes
out first (terminal#7).

## Ownership and the way out

- Terminals outlive sockets: a closed socket only drops its subscriptions (terminal#5).
- `terminal/close` disposes the PTY (SIGKILL to its process group, then closes the master) and drops the ring.
- `TerminalHost` is disposed when the host stops, and closes every terminal (terminal#8).
- `owner` is the chat session that asked; a start with an owner whose terminal is still running answers that one
  (terminal#9). It's passed to the shell as `ASSEMBLY_SESSION_ID`, as the old app passed `COMMANDBAR_SESSION_ID`.

## Environment and arguments

`TERM=xterm-256color`, `COLORTERM=truecolor`, `TERM_PROGRAM=Assembly`, and `NO_COLOR` removed, as the old app did.
The shell is `shell`, else `$SHELL`, else `/bin/sh`, always started with `-l` (every common shell takes it) so the
terminal has the user's full PATH, plus `-c <command>` when there's a command. A blank command is no command.

## What waits for later steps

- Remote (terminal#10): `TerminalFeature` registers on every machine (`MachineFeatureList`), so a remote host serves
  the same calls. Reaching it from the desktop's window comes with C2's socket relay, which forwards a machine's calls
  and topics; nothing terminal-specific is needed there.
- Agent tool terminals (ACP `terminal/*`) stay separate, as P22 says.
- Closing a session's terminals when the session is deleted comes with that delete.
- The window side (the Ghostty view and its socket calls) is the GPUI lane's.

## Open questions for Jesse

- `ASSEMBLY_SESSION_ID` as the owner variable's name.
- `/bin/sh` as the last fallback shell (the old app used `/bin/zsh`).

# Terminal: later steps (window, Run, sign-in)


Draft research by sub-agents at 3cb50fa, before the review fixes: line numbers in the terminal files may have moved.
Research, 2026-10-27. `old/` = mac-command-bar `2089290:tauri-svelte-preview/src/lib/` (not `app/src` at that commit); `lane/` = `worktrees/Assembly/terminal/`; `F/` = `lane/dotnet/Assembly.Application/Features/Terminal/`.

## 1. Old behaviors the window must keep

1. One terminal per session, lazily, in the right panel (`old/shell/components/RightPanel.svelte:167-171`).
2. Reopen finds the session's own terminal: a running one is reused, an exited one is replayed, closed, then a fresh one started (`old/shell/terminal/WorkspaceTerminal.svelte:134-185`).
3. Replay the last 4 MB; queue live output meanwhile, drop what replay covered (`WorkspaceTerminal.svelte:31,81-131`).
4. Size before replay, so old text wraps at the width it was written (`old/shell/terminal/terminalService.ts:466-476,606-610`); skip duplicate resizes (`:574-581`); clamp 20-300 x 4-100 (`:365-368`).
5. Keystrokes: one write in flight, typed keys coalesced into the next (`terminalService.ts:277-296`); focus in/out sequences are real input (`:257-265`).
6. Hiding or reloading never kills a terminal; only an explicit close does (`terminalService.ts:14-18,1026-1032`).
7. Exit shows "Terminal exited" and stops input; output stays readable (`WorkspaceTerminal.svelte:93,247`; `terminalService.ts:194-197`).
8. Theme change recolors (`RightPanel.svelte:169`).
9. Run panel: a 200-line / 64 KB plain-text tail, escapes stripped, seeded from 64 KB of scrollback, then live (`old/shell/panels/run/runOutputTail.ts:18-34`; `old/shell/stacks/runOutputService.ts:38,107-135`).
10. Run as the command itself, so the exit code is the command's (`terminalService.ts:178-187`; `old/shell/stacks/stackService.ts:196-199,215`); stopping leaves a finished row (`stackService.ts:231-234`).
11. Chat to terminal: bracketed paste, then Enter as a SEPARATE write, or Claude's TUI never submits (`old/shell/conversation/conversationService.ts:1599-1608`); only with the terminal writer lease (`:1589-1594`).
12. Terminal transcript projection is a transcript file watcher, not PTY work (`conversationService.ts:600-648`).

`createTerminalService` had no caller outside its file (assumed, from `git grep`); the live UI was `WorkspaceTerminal`.

## 2. Ghostty client against the new wire

- Start with `{cwd, cols, rows, owner}`; use the answered size (`F/TerminalHost.cs:80-81`).
- Subscribe `terminal` with `after` = last byte offset held. The state frame's seq is where live rows continue (`lane/dotnet/Assembly.Infrastructure/Socket/SocketDispatcher.cs:100-105`). If the gap was over 1 MB or trimmed, seq is the end and no row comes (`F/RunningTerminal.cs:228`): call `terminal/read` from `after` (raised to the last 4 MB, `F/OutputRing.cs:334`) until `end` reaches that seq.
- Keep a high-water offset; apply only bytes above it. A piece starting past it means trimmed bytes: show a gap.
- One `terminal/write` in flight per terminal (`F/TerminalFeature.cs:8-9`), but never merge paste and Enter (item 11).
- Exit row: `seq` is end + 1, no data (`F/RunningTerminal.cs:233,268`): never use it as an offset. Writes after exit fail (`:200-201`).
- Close: `terminal/close`; the topic goes silent with no closed signal, and a sub to an unknown id gets an empty state (`F/TerminalHost.cs:125,129-136`).
- Native view: hidden for overlays/drags, released on tab close (`g0-host/desktop/src/core/native_views/native_views.rs:1-6`); resize after a drag.

Missing from the wire:
- The notes promise a `terminals` topic (`lane/dotnet/notes/terminal-shape.md:65-68`); the code has only `terminal/list` (`F/TerminalFeature.cs:23`), so new or closed terminals aren't pushed.
- No kind (old: `old/tauriSource.ts:101`). Owner reuse answers ANY running terminal with that owner (`F/TerminalHost.cs:98`), so a Run or sign-in terminal under a session id comes back as its shell.
- Signal folded into 128 + n (`terminal-shape.md:44`); old sent it apart (`old/tauriSource.ts:100`).
- No env or argv on start, just a shell string (`F/StartArgs.cs:4`).
- Title (OSC 0/2), cwd (OSC 7), bell, OSC 8 links, OSC 52 clipboard and prompt marks (OSC 133) are client-side, so a terminal with no view has none of them (assumed, since the backend never parses, `terminal-shape.md:9-12`).
- Assumed risk: libghostty's embedded surface normally starts its own PTY; here the backend owns it, so the client needs a byte-fed mode (e.g. libghostty-vt). No lane has Ghostty code yet, only `g0-host`'s native-view stubs (`native_views.rs:28-63`, `todo!()`).

## 3. Later features

- P7 / P32 Run: saved definitions kept apart from running terminals, on the owning machine, reusing terminal lifetime, "argument-safe launch", truthful exit (`lane/docs/# Assembly .md:590-594`; `lane/docs/plans/2026-10-26/implementation-plan.md:1745`). Needs a command start that doesn't collide with owner reuse, env, and the tail view.
- P8a sign-in: Claude's login in an Assembly terminal; remote sign-in URLs opened on Jesse's computer (`implementation-plan.md:1747-1758`). Needs a URL to reach the window (OSC 8 or the output itself) and the `open_url` intent (`g0-host/desktop/src/core/intents/open_url.rs`).
- P22: ACP tool terminals stay separate (`# Assembly .md:514`; `terminal-shape.md:98`); old mirrored them as `AgentTool` PTYs (`old/tauriSource.ts:103-108`).

## 4. Tests for the later steps

- Resubscribe 0, 500 KB, 3 MB, 20 MB behind, with output racing replay: bytes equal the ring tail, none repeated or missing; a split character draws whole.
- Exit after a burst: all output first; input refused.
- Chat paste and Enter arrive as two writes; typing during a slow write stays in order.
- Resize while hidden or dragging: one resize, after.
- Reopening an exited session's terminal closes it and starts fresh.
- A Run terminal for session S doesn't become S's shell.
- Run exit code and signal are the command's; tail strips escapes, caps at 200 lines.
- Remote via the C2 relay: same flow; OSC 52 and an OSC 8 sign-in URL reach the Mac.
- Another client's close: the view notices.

## 5. Open questions for Jesse

1. Add the `terminals` topic, or poll `terminal/list`?
2. A `kind` (or scoped owner) to keep Run and sign-in terminals out of owner reuse?
3. Send the signal separately from the exit code?
4. A live Ghostty surface per hidden terminal (title, bell), or only the front one?
5. OSC 52: allow writes only, ask first, or block?
6. Does transcript projection survive?
7. Several terminals per session (tabs), or one as before?
8. Ghostty embedding: byte-fed libghostty-vt?

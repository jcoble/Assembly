# C# and Roslyn in Mac Command Bar

## Outcome

Mac Command Bar now uses the official Roslyn language server for C# inside the
existing editor and session architecture. This is not a second IDE project and
does not add a WebSocket server. Monaco continues to call Tauri commands, Rust
owns a bounded pool of persistent language-server processes keyed by language
and workspace root, and the existing session rail owns Build and Test output.

The development machine currently has `roslyn-language-server` 5.11.0-1.26379.6
installed as a global .NET tool. Shipping that executable with a distributable
app is still a packaging task; the current Tauri configuration has bundling
disabled.

## The repository as it exists

| Area | Current implementation |
| --- | --- |
| Desktop framework | Tauri 2 (`tauri` 2.11.2 in the verified build) |
| Frontend | Svelte 5.56, SvelteKit 2.64, Vite 8, TypeScript 6 |
| Editor | Monaco 0.55 with custom providers |
| Shell layout | Dockview 6.6 plus the primary Svelte shell |
| Styling | Tailwind CSS 4 plus shell-owned CSS |
| Rust entry point | `src-tauri/src/main.rs` |
| Language-server host | `src-tauri/src/lsp.rs` |
| Session scanner | the sibling `core` crate, chiefly `core/src/scanners/sessions.rs` |
| Terminal host | Rust-owned portable PTYs through the existing terminal registry |

The root page is the coordinator. Svelte rune stores hold UI state, focused
services own file/language-server/terminal I/O, and components ask the page for
cross-panel actions. Build and Test follow that rule: the editor requests a
fixed workspace action, the page creates a normal owned session, and the
terminal service starts it as a direct command.

## Session and agent model

The scanner reads existing Codex, Claude, and cmux session files rather than
inventing a new conversation format.

The Rust scan record carries:

- provider and provider-native session id;
- title, description, model, project path, and last activity;
- resume commands;
- inferred branch, Notion task, pull request, and source label;
- bounded message count and latest user/agent preview.

Providers currently include `codex`, `claude`, `cmux-codex`, and
`cmux-claude`. The frontend normalizes these into an `OwnedSession`.
`ownedId`, minted by Mac Command Bar, is the stable primary key. A provider's
native id is metadata because resuming an agent may produce a different native
id. The same record tracks the PTY id, live/background/exited state, completion
state, project directory, resume command, branch/task/PR hints, and whether the
session came through cmux.

Each `ownedId` also has a `SessionWorkspaceSnapshot`: open editor tabs, active
file, expanded and selected explorer nodes, explorer scroll position, and diff
selection. That is why Build/Test is represented as an ordinary owned session
instead of an editor-only subprocess: it gets the same output, exit state,
selection behavior, persistence, and workspace boundary as every other piece
of work.

## Monaco and file identity

Monaco already uses real file models:

```ts
const uri = monaco.Uri.file(preview.path);
```

Definitions, references, Peek models, diagnostics, completion, symbols,
formatting, rename, code actions, signature help, inlay hints, and semantic
tokens are custom Monaco providers backed by Tauri commands. No
`monaco-languageclient` or browser WebSocket bridge is needed.

Reference CodeLens is also semantic. The row is anchored by
`textDocument/documentSymbol`; its number and Peek result share the same
Roslyn `textDocument/references` answer. The editor now waits for the
authoritative language-server anchors instead of briefly painting a second set
from its text parser. Anchors and painted counts are cached by the model's
`file://` URI, so a number does not disappear and reappear during the
parser-to-Roslyn handoff or an ordinary tab switch.

The native editor has one reference authority. Both the margin count and Peek
use Roslyn; they do not fall back to the older Rust name scan. That legacy
counter remains implemented and tested for the browser/older preview, but it
is not constructed by a native editor service. Monaco also assigns a
monotonic id to each count request, so an older asynchronous no-answer cannot
repaint over the newer Roslyn total. The pending row still says `0 references`,
matching VS Code.

## Roslyn process and transport

Rust starts:

```text
roslyn-language-server
  --stdio
  --autoLoadProjects
  --telemetryLevel off
```

The existing `SourceLspRegistry` owns each child process and its stdin/stdout
JSON-RPC stream. Sessions are keyed by both language and canonical workspace
root. Up to five recent roots stay warm for each language, matching the normal
three-to-five-workspace switching pattern. Returning to a recent root reuses
its indexed Roslyn process; opening a sixth root evicts only the least recently
used slot. A background router matches concurrent response ids, receives
diagnostics and progress events, handles cancellation, retains a bounded server
log, and terminates child processes when their connection is dropped.

Each language server starts in its own process group. This matters for Roslyn
because it starts MSBuild BuildHost helpers: stopping only the top-level
`roslyn-language-server` process can orphan those helpers. Rust now stops and
reaps the entire owned group on pool eviction or app shutdown.

The initialize exchange now supplies:

- this process id and Mac Command Bar client information;
- `rootPath`, `rootUri`, and a workspace folder;
- workspace configuration and folder capabilities;
- work-done progress;
- pull-diagnostic support.

Roslyn configuration enables open-file compiler/analyzer diagnostics and
reference CodeLens data. Roslyn's own test CodeLens commands remain disabled
because Mac Command Bar exposes Build and Test through its session system.

### The document-sync fix

The old bridge sent a full-text `didChange` before every lookup, even when the
text had not changed. It also omitted a range when a server advertised
incremental synchronization. Roslyn rejected that shape and could terminate
inside its range conversion.

Rust now retains the last text and version for every open URI:

- the first question sends `didOpen`;
- another question against unchanged text sends no document notification;
- changed text increments the version;
- an incremental server receives one valid whole-document replacement with a
  UTF-16 LSP range;
- a full-sync server receives the normal full-text change.

Roslyn can briefly cancel a valid request while replacing its first project
snapshot. The bridge retries that specific server-cancel response once after a
short delay.

## Build and Test CodeLens

C# models get two fixed rows at the first line:

- **Build workspace** runs `dotnet build --nologo`;
- **Test workspace** runs `dotnet test --nologo`.

The active `editorState.projectRoot` is passed only as the PTY working
directory. It is never interpolated into shell text. The root page reuses
`onStartStack`, so each click creates a fresh owned session, runs the command
directly through the Rust terminal backend, selects the session, shows its
terminal, and records the real exit state.

This is intentionally workspace-level rather than guessing which `.csproj`
owns the current file. Solution/project discovery and per-test method lenses
can be layered on later without changing the transport or session boundary.

## A .NET developer's map of the Rust

| Rust in this app | Familiar .NET analogy |
| --- | --- |
| `struct` with `serde` derives | POCO/record plus JSON attributes |
| `Option<T>` | an explicit nullable value that must be handled |
| `Result<T, String>` | a return value carrying either success or an error, rather than an exception crossing the Tauri boundary |
| `Arc<T>` | a thread-safe shared reference, similar in purpose to a shared singleton reference |
| `Mutex<T>` | `lock`/`Monitor` around mutable shared state |
| `AtomicI64` | `Interlocked` counter for JSON-RPC ids |
| `HashMap<(language, root), session>` | a keyed singleton/cache where the workspace path is part of the service identity |
| `Command` and `Child` | `ProcessStartInfo` and `Process` |
| `Drop` | deterministic cleanup when the last owner goes away, comparable to `Dispose` being called by ownership rules |
| `mpsc` channel | `Channel<T>` used to route replies from the reader thread |
| Tauri `State<T>` | application-scoped dependency injection state |
| `match` | exhaustive pattern matching over values and variants |

The ownership design matters more than the syntax: `SourceLspRegistry` owns a
bounded keyed cache of language sessions, each session owns a connection, the
connection owns the Roslyn child, and dropping an evicted or final connection
shuts the child down. That keeps process lifecycle out of Svelte and gives Rust
one place to enforce reuse, eviction, timeouts, versions, routing, and cleanup.

## Verification performed

- TypeScript/Svelte static check: passed.
- Primary shell Svelte check: 0 errors and 0 warnings.
- Production Vite build: passed.
- Rust language-server suite: 53 passed, including workspace-key identity,
  independent per-root readiness state, recent-root reuse, least-recently-used
  sixth-root eviction, and owned process groups.
- Real Roslyn smoke: passed in isolation against a restored C# project,
  exercising symbols, hover, definition, references, and diagnostics.
- Rust debug build: passed.
- Tauri release build: passed; executable produced under
  `src-tauri/target/release`.
- Native app process: rebuilt and left running for interactive
  verification. Each opened C# workspace gets its own warm Roslyn slot, up to
  five recent roots.
- CodeLens regression checks: passed for workspace commands, stable URI/model
  identity, stale-result rejection, native single-authority references,
  batching, and language-server status.
- Roslyn child cleanup: 22 helpers orphaned by earlier workspace re-roots were
  removed; the live smoke suite then left zero orphaned BuildHost processes.
- Playwright web-preview proof: `AdminAlertConfiguration.cs` rendered the
  VS Code-style `0 references` placeholder, settled to `35`/`50`, and opened a
  Peek tree with 35 symbols in 17 files. Switching EdiPlatform to MacCommandBar
  and back restored the file and its settled counts with zero page errors.
  This browser proof exercises the intentionally retained fallback counter;
  the native Roslyn-only boundary is covered by the Rust and static regression
  tests.

The complete LSP suite is intentionally run with one test thread. Running all
real language-server smoke projects concurrently caused the older TypeScript
diagnostic smoke to miss its short diagnostic window; its isolated run and the
serial suite both pass.

## Exact implementation files

| File | Change made for this port |
| --- | --- |
| `src-tauri/src/lsp.rs` | Replaced the C# server spec with Roslyn, expanded initialization/configuration, corrected document synchronization, added the bounded cancellation retry, owns the complete Roslyn process group, and keeps a five-root least-recently-used pool per language. |
| `src-tauri/Cargo.toml`, `src-tauri/Cargo.lock` | Declares the small Unix process-control dependency used to stop Roslyn and its BuildHost children together. |
| `src/lib/workspaceCodeLens.ts` | Added the fixed, path-safe Build/Test action descriptions used by the editor and session shell. |
| `src/lib/MonacoSourceEditor.svelte` | Added Build/Test rows and command handlers; made language-server anchors authoritative and URI-cached; rejects stale count completions so Peek cannot be followed by a repaint to zero. |
| `src/lib/sourceCodeLensKeys.ts` | Keeps the VS Code-style zero placeholder and owns the pure settled-count rule used by Monaco. |
| `src/lib/shell/components/EditorPanel.svelte` | Binds Build/Test to the active `projectRoot` and asks the shell to start an owned command session. |
| `src/routes/+page.svelte` | Reuses the existing direct terminal-session starter for workspace commands. |
| `scripts/workspaceCodeLens.test.mjs` | Covers fixed commands, working-directory isolation, C# gating, and session wiring. |
| `scripts/sourceCodeLensKeys.test.mjs` | Adds the no-competing-anchor, no-native-text-counter, and stale-result regression assertions. This file already contained in-progress TSK-799 work. |
| `package.json` | Registers the new workspace CodeLens check. The adjacent project-index script entry belongs to the existing dirty WIP. |
| `src/routes/+page.svelte`, `scripts/sourceUi.test.mjs` | Updates the old-shell install guidance from `csharp-ls` to `roslyn-language-server`. The route already had a very large user-owned edit. |
| `src/lib/shell/editor/sourceIntelligence.ts`, `src/lib/shell/components/editor/languageServerStatus.test.ts` | Makes native references Roslyn-only while preserving the legacy name scanner for the browser preview, and updates C# server wording/fixture names. The intelligence service already contained substantial TSK-799 WIP. |
| `docs/C-SHARP-ROSLYN-INTEGRATION.md` | This architecture, operation, verification, and .NET-to-Rust guide. |

No unrelated dirty file was reverted, reformatted wholesale, staged, or
committed. In particular, the project-index, persistence, batching, explorer,
root-route, and `.vscode` changes visible in `git status` remain user-owned WIP.

## Known WIP and boundaries

1. Roslyn is a global development tool, not yet bundled or automatically
   provisioned. A production installer must pin and ship or acquire it.
2. Tauri bundling is currently disabled in `tauri.conf.json`; this work
   produces the application executable, not a signed/notarized `.app`.
3. The custom rows are workspace-level Build/Test actions. Per-project,
   per-class, and per-method test discovery is not implemented.
4. The live Roslyn smoke proved diagnostic analysis on the broken line. The
   exact compiler-error squiggle category was not stable in Roslyn's standalone
   pull-diagnostic response, so this work does not claim parity with every
   diagnostic category shown by Visual Studio.
5. The restored workbench is now the root application shell. The legacy root
   shell and alternate `/next` launch path are retired.
6. The work remains in the user's existing dirty `tsk-799-code-intelligence`
   worktree. No commit, push, task closure, or user-owned WIP cleanup was
   performed.
7. Keeping up to five Roslyn processes warm per language intentionally trades
   memory for fast workspace returns. A sixth distinct root closes the least
   recently used process and its BuildHost group.
8. A cold workspace still follows the normal VS Code-style transition from
   `0 references` to Roslyn's final count. Workspace progress and readiness are
   now keyed by both language and canonical root, so another warm workspace can
   no longer reset or release that placeholder.

## Developer setup

Install the preview Roslyn tool used by this implementation:

```bash
dotnet tool install --global roslyn-language-server --prerelease
```

If it is already installed:

```bash
dotnet tool update --global roslyn-language-server --prerelease
```

Run the focused native suite without competing language-server projects:

```bash
RUST_TEST_THREADS=1 MSBUILDDISABLENODEREUSE=1 pnpm test:native-lsp
```

Run the current desktop shell:

```bash
pnpm dev
pnpm tauri:dev
```

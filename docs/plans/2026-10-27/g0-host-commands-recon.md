# g0-host recon: commands, key bindings, the Cmd-K palette and intents

Read-only recon, 2026-10-27. Old app = `~/dev/work/mac-command-bar/tauri-svelte-preview/src` at d5f5fe68c (paths below are
relative to it). New app = `desktop/` in this worktree. Kit paths are under
`~/.cargo/registry/src/index.crates.io-1949cf8c6b5b557f/` (shortened: `component/` = `gpui-component-0.8.1/src/`,
`base/` = `gpui-base-0.8.1/src/`, `gpui/` = `gpui-pre-0.3.10/src/`). Nothing was built or run.

## 0. What already exists in this worktree (uncommitted stubs)

- `desktop/src/core/commands/command_registry.rs:7-21`: `CommandRegistry { items: Vec<CommandItem> }`, a `Global`, with
  `add(item, cx)` and `items(cx)`, both `todo!()`.
- `desktop/src/core/intents/{open_file,open_url,open_diff,open_session}.rs`: four `#[derive(Action)]` structs.
  `OpenFile{path, line: Option<u32>}`, `OpenUrl{url}`, `OpenDiff{path}`, `OpenSession{id}`. No `#[action(namespace=..)]`.
- `desktop/src/features/palette/mod.rs:6`: `actions!(palette, [TogglePalette])`. `palette_view.rs:13-19`: `init` and `new`, `todo!()`.
- `desktop/src/core/native_views/native_views.rs:47-52`: `overlay_opened` / `overlay_closed`, `todo!()`. The palette must call them.
- `desktop/Cargo.toml:7-11` has `gpui-kit`, `gpui-fps`, `serde`, `serde_json`, `tungstenite`. No `schemars` (see risk 1).
- `docs/behavior/g0-host.md` is cited by `native_views/mod.rs:6` but does not exist (`docs/behavior/` holds only `base.md`).

## 1. Command and shortcut table (old app)

### 1a. Palette commands

Important finding: `registerShellCommands` (`lib/shell/shellCommands.ts:47`, 22 commands) is defined but nothing calls
it (grep for `registerShellCommands` over `src/` finds only its definition). So the old palette that actually ran listed
only the 3 commands `PalettePanel.svelte` seeds (`lib/shell/components/PalettePanel.svelte:138-160`). The 22 are
"what the old code meant to offer"; Jesse has to say which to carry over (question Q1).

| Command (old label) | id | Old file:line | Owning lane |
|---|---|---|---|
| Reset layout | reset-layout | PalettePanel.svelte:140-145 (wired `routes/+page.svelte:939`) | g0-shape |
| Show bottom dock | show-bottom-dock | PalettePanel.svelte:153-158 (`+page.svelte:943`) | g0-shape |
| Open settings | open-settings | PalettePanel.svelte:146-152 (`ShellOverlays.svelte:163`) | settings (later) |
| Show the terminal (really: bring the chat tab forward) | show-session | shellCommands.ts:51-56 | g0-session |
| Start a new session | new-session | shellCommands.ts:93-98 | g0-session |
| Show the editor | show-editor | shellCommands.ts:57-62 | files (later) |
| Close the file in the editor | editor-close-file | shellCommands.ts:194-200 | files (later) |
| Refresh the file list / Stop listing files | explorer-refresh, explorer-stop-scan | shellCommands.ts:127-140 | files (later) |
| Look for problems again / Show the problems list at the bottom | problems-refresh, problems-show-at-bottom | shellCommands.ts:168-183 | files (later); the bottom-strip one also g0-shape |
| Show source control | show-git | shellCommands.ts:63-68 | git (later) |
| Source control: refresh, fetch, pull, push | git-refresh, git-fetch, git-pull, git-push | shellCommands.ts:99-126 | git (later) |
| Show the changes to a file | show-diff | shellCommands.ts:75-80 | git (later) |
| Clean up worktrees | worktrees-clean-up | shellCommands.ts:150-155 | git (later) |
| Show the browser / Reload the web page | show-browser, browser-reload | shellCommands.ts:69-74, 184-193 | browser (later) |
| Show the Session Library | show-session-library | shellCommands.ts:81-86 | history (later) |
| Show the Agents panel | show-agents | shellCommands.ts:87-92 | agents (later) |
| Show run configurations / Run configurations: check ports | show-stacks, stacks-refresh | shellCommands.ts:156-167 | run (later) |
| Show leftover browser processes | playwright-show | shellCommands.ts:141-149 | resources (later) |

Old palette behaviour to keep or consciously drop:
- A command has `id`, `label`, `detail` (a second line), optional `disabled()` re-checked at search time, `perform()`
  (`lib/shell/palette/commandRegistry.ts:20-31`). A throwing `disabled()` counts as disabled (:98-105).
- Registered per source name; re-registering a source replaces it, a duplicate id: first one wins (:50-62, :79-90).
- Search: every word of the query must appear, any order, in label or detail (:116-129). At most 12 rows (:139).
- Disabled rows stay in the list greyed out (:136-137). A failed command shows "X did not finish: ..." (PalettePanel.svelte:76-84, :179-184).
- Rows are re-read from the registry each time the palette opens (PalettePanel.svelte:41-43, :67).
- Overlay: placeholder "Run command", a `Cmd+K` hint, "No matching commands" (`lib/components/overlays/CommandPaletteOverlay.svelte:94-102`).

### 1b. Global shortcuts

| Key | Does | Old file:line | Owning lane |
|---|---|---|---|
| Cmd/Ctrl+K | open the palette | PalettePanel.svelte:123 | g0-host |
| Cmd/Ctrl+Shift+P | open the palette (second key) | PalettePanel.svelte:123 | g0-host |
| Escape (palette open) | close it; Up/Down/Enter move and run | PalettePanel.svelte:93-117, :130-133 | kit `Command` (g0-host just hosts it) |
| Cmd+Q | quit, via an app menu item | `src-tauri/src/main.rs:3368-3395` (Tauri, macOS only) | g0-host (needs a Quit action; `desktop/src/main.rs:12` only sets `LastWindowClosed`) |
| Cmd/Ctrl+P "Open source file" | quick open | QuickOpenOverlay.svelte:3, but the component is imported nowhere (grep) | dead; files (later) if wanted |
| user-set Run shortcuts, e.g. `Cmd+Shift+R` | start a run action from anywhere | RunPanel.svelte:218-227, keybindingCapture.ts:1-30 | run (later) |

Local shortcuts (belong to the panel that owns the text field, not to g0-host):
Escape closes settings (`lib/shell/settings/SettingsDialog.svelte:636-640`), the usage popover (`lib/shell/usage/UsagePopover.svelte:125-130`),
the resource manager (`lib/shell/resources/ResourceManagerPanel.svelte:132-135`), the Messages, Projects and Workflows screens
(`lib/shell/messages/MessagesScreen.svelte:36`, `lib/shell/projects/ProjectsScreen.svelte:312`, `lib/shell/workflows/WorkflowsScreen.svelte:251`),
and the plan chip (`lib/shell/conversation/components/ConversationComposer.svelte:175-177`).
Enter sends and Shift+Enter makes a newline, Up/Down/Escape in the slash menu: ConversationComposer.svelte:308-330 (g0-session).
Cmd/Ctrl+Enter commits: `lib/shell/git/components/ChangesPane.svelte:133`, `.../sourceControl/SourceControlPanel.svelte:566` (git).
Cmd/Ctrl+Enter: `AddProjectDialog.svelte:198`, `panels/tasks/NotionTaskViewer.svelte:223`. Cmd/Ctrl+S: `files/components/SourceMarkdownPreview.svelte:173` (files).

## 2. Intent senders and handlers (old app)

All four go through `lib/shell/workbenchNavigation.ts`, which the page fills in once (`routes/+page.svelte:371-401`), plus the older
file bus `lib/shell/openFileBus.ts`. A call made before a handler exists is a no-op, except two that are parked: a file request
(last one wins, `openFileBus.ts:56-58`, delivered at `:64-69`) and a browser URL (`workbenchNavigation.ts:113`, `:321`).

| Intent | Handler (the one owner) | Senders |
|---|---|---|
| Open file | Editor: `files/components/EditorPanel.svelte:1585` subscribes with `onOpenFile`; the sender also brings the editor tab forward (`workbenchNavigation.ts:166-169`) | Files panel `FilesPanel.svelte:748,790,803,1357`; changed-file row `git/.../ChangedFileRow.svelte:143`; git history `GitHistoryView.svelte:294`; commit timeline `CommitTimeline.svelte:165`; commit pane `ChangesPane.svelte:122,128`; diff view `GitDiffView.svelte:183`; PR workspace `PullRequestWorkspace.svelte:400`; problems `ProblemsPanel.svelte:117`; session context `SessionContextPanel.svelte:173`; history logs `HistoryPanel.svelte:566,702`; chat file links `ConversationSurface.svelte:655`, `TasksPanel.svelte:318`, `AgentsPanel.svelte:273` (via `requestOpenConversationFile`, `openFileBus.ts:77-123`); `assembly://file?path=` and a file opened from Finder (`workbenchNavigation.ts:266-271`, `:286`, `:291-295`) |
| Open URL | Browser: `browser/components/BrowserPanel.svelte:355` registers `registerBrowserUrlNavigation` (`workbenchNavigation.ts:132-142`) | chat links `ConversationMessage.svelte:111,113`, `ToolItem.svelte:122`; `RunPanel.svelte:189,312`; `SourceMarkdownPreview.svelte:305`; workflows `WorkflowStepPanel.svelte:132`, `WorkflowStepDetails.svelte:139`, `WorkflowTimeline.svelte:69`; `NotionTaskViewer.svelte:354`; `BoardCards.svelte:135`; `assembly://open?url=` (`workbenchNavigation.ts:262-265`, `:285`) |
| Open diff | git: `routes/+page.svelte:375-378` calls `gitService.showStoredDiff(projectRoot, relativePath)`, then the diff tab comes forward (`workbenchNavigation.ts:172-175`) | only `ChangedFileRow.svelte:133` |
| Open session | page: `routes/+page.svelte:465-476` (`selectSession`), registered at `:371-373` | `sessionRowJump` (`components/sessionRowJump.ts`, registered `+page.svelte:364-370`) from `AgentsPanel.svelte:262`, `AgentMessages.svelte:250`, `WorkflowsScreen.svelte:212`, `WorkflowRuns.svelte:406`, `workflows/ownerInboxActions.ts:41`, `WorktreesPanel.svelte:324`, `PullRequestWorkspace.svelte:720`; `assembly://session/<id>` deep link (`workbenchNavigation.ts:255-257`, `:283`); `newSessionController.svelte.ts:100` |

Deep links reach the app from the OS: `routes/+page.svelte:420-445` (`onOpenUrl` plus the launch URL), which waits for the first
session to be selected. This is the "launcher" half of g0-host: something must turn an `assembly://` or `file:` URL into one of the four intents.

Extra behaviour hidden inside the old open-URL path (`workbenchNavigation.ts:303-323`): an `assembly:` URL is re-dispatched; a Notion link opens the
Tasks panel or the Notion app (:217-233); a github.com pull request link goes to the Pull requests tab (:184-197, :310-315). Others open the browser.
There is a fifth sender-handler pair, `openFileTimeline` (:178-182, senders `FilesPanel.svelte:967,1368`, `EditorPanel.svelte:1396`), not in the four-intent list.

## 3. Kit APIs to use (verified by reading the source)

Actions with data (gpui-pre):
- `#[derive(Action)]` needs `Clone + PartialEq`, plus `serde::Deserialize` and `schemars::JsonSchema` unless `#[action(no_json)]`: `gpui/action.rs:62-71`. Options `namespace`, `name`, `no_json`, `no_register`: `:73-83`.
- `actions!(ns, [A, B])` makes unit structs: `gpui/action.rs:25-33`. Two actions with the same registered name panic when the `App` is created: `gpui/action.rs:54`.
- Global handler: `App::on_action::<A>(listener: Fn(&A, &mut App))`, `gpui/app.rs:2760-2774`. It gets no `Window`.
- Send: `Window::dispatch_action(Box<dyn Action>, cx)` is queued with `cx.defer`, not run at once: `gpui/window.rs:2558-2571`. `App::dispatch_action(&dyn Action)` uses the active window, else global listeners only: `gpui/app.rs:2963-2976`.
- Is anything listening: `App::is_action_available(&dyn Action)`: `gpui/app.rs:2907-2920` (true if a global listener exists for the type).
- Keys: `cx.bind_keys([KeyBinding::new("cmd-k", A, None)])`: `gpui/app.rs:2741-2745`; `KeyBinding::new` is `gpui/keymap/binding.rs:35-46`; `KeyBinding::load(.., cx)` resolves the `secondary-` alias per OS: `binding.rs:51-63`, `gpui/platform/keystroke.rs:124-165`. `cx.clear_key_bindings()` clears everything: `app.rs:2747`.

The palette (gpui-component):
- `Command::new(&Entity<CommandState>)` with `.item/.items/.group/.separator/.placeholder/.on_query/.on_select/.on_confirm/.on_cancel/.empty/.header/.footer/.bordered(false)`: `component/command/command.rs:89-255`.
- `CommandState::new(window, cx)`, `query(cx)`, `set_query(..)`, `selected_index()`, `matched_count()`, `focus(window, cx)`: `component/command/state.rs:146, 226, 234, 256, 301, 306`.
- `CommandItem::new().label(..).icon(..).action(Box<dyn Action>).keywords([..]).disabled(bool).child(|w,cx| ..)`: `component/command/item.rs:37, 50, 56, 64, 80, 95, 169-174`. The row also shows the action's current key binding (`item.rs:61-63`). That is how a key and a label appear together.
- Keys inside the palette (context `"Command"`, `state.rs:28`): escape, enter, up, down: `state.rs:62-69`. Escape clears a non-empty query first, then runs `on_cancel` and propagates: `state.rs:602-615`. Confirm dispatches the item's action with `window.dispatch_action`, then runs `on_confirm` deferred: `state.rs:617-638`.
- Open as an overlay: `use gpui_kit::component::WindowExt; window.open_dialog(cx, |dialog, window, cx| ..)`: `component/window_ext.rs:75-79` (trait), impl `:176-184`; underneath `WindowState::open_dialog` `component/root.rs:375-402`. `has_active_dialog`, `close_dialog`, `close_all_dialogs`: `window_ext.rs:109, 112, 115`, `root.rs:447, 493`. The `Command` is meant to sit in a dialog (`command.rs:224-231`, `state.rs` Escape comment).
- Dialog knobs: `.title`, `.content(..)`, `.close_button(false)`, `.overlay_closable`, `.keyboard` (Esc closes), `.margin_top`, `.w`, `.entrance`: `component/dialog/dialog.rs:402, 482, 533, 539, 488, 505, 549`. `.on_close(Fn(&ClickEvent, ..))` at `dialog.rs:451-457`. The base dialog calls it after Escape (`base/dialog.rs:571-584`), after confirm (`:596-598`) and on a backdrop click (`:632`). This is the hook for `NativeViews::overlay_closed`.
- Window setup: `gpui_kit::open_window` wraps the page in `base::Root`, and `gpui_kit::init(cx)` registers the dialog layer: `gpui-kit-0.8.1/src/lib.rs:164-172, 188-198`. `desktop/src/main.rs:13-22` already does both.
- Test support: `gpui_kit` feature `test-support`; `TestAppContext::{update, run_until_parked, dispatch_action, simulate_keystrokes}`: `gpui/app/test_context.rs:271, 600, 605, 622`; window version `:878, 895, 900, 924`. The kit's own tests are good models: `component/command/state.rs:1177-1430`, `item.rs:294-332`.

## 4. Proposed design, short (what the code step builds)

- One `CommandRegistry` global that keeps, per command: id, label, keywords, an enabled check, a key hint, and the action to dispatch. `PaletteView` builds `CommandItem`s from it each time it opens (the old app also re-read on open).
- `TogglePalette` bound to `cmd-k` on macOS, `ctrl-k` elsewhere, with the handler on `cx.on_action` using `cx.active_window()` (`gpui/app.rs:1558`) to open the dialog.
- One `IntentOwners` check in `core/intents/`: a feature registers as the owner of `OpenFile` / `OpenUrl` / `OpenDiff` / `OpenSession`; a second registration of the same one panics at start with both names in the message. The owner then calls `cx.on_action`.

## 5. Proposed test list (behaviour only)

Registry and palette (headless, `gpui_kit` `test-support`):
1. Typing part of a label leaves only the commands whose label (or keywords) contain it; clearing the text brings all back.
2. Typing text that matches nothing shows the empty message and Enter does nothing.
3. Enter on the highlighted command dispatches that command's action and the app handler for it runs once (after `run_until_parked`).
4. Clicking a row does the same as Enter.
5. A disabled command is shown but Enter skips it and dispatches nothing; it turns enabled when the check changes and the palette is reopened.
6. Two commands registered under the same id: the second fails loudly (or, if Q3 says first-wins, the list holds one).
7. Commands registered by two features both appear, in registration order.
8. `Cmd-K` (`Ctrl-K` off macOS) opens the palette; pressing it again closes it; Escape clears the text first, then closes.
9. Opening tells `NativeViews` an overlay opened; closing by Enter, Escape or a backdrop click tells it closed, exactly once each.
10. A command's key binding shows beside its label (the kit's row hint) and pressing that key runs the same action.

Intents:
11. Dispatching `OpenFile{path, line}` reaches its single owner with the same data; same for the other three.
12. Registering a second owner for the same intent fails loudly at start, naming both.
13. An intent with no owner does nothing and reports that, rather than silently vanishing (decide: log or error).
14. Four intents, four different owners, none interfere.
15. Launch link: `assembly://session/<id>`, `assembly://open?url=..`, `assembly://file?path=..`, `file:///..` each produce the right intent; an unknown link produces none (ports `parseAssemblyLink`, `workbenchNavigation.ts:246-278`).

## 6. Risks

1. Cargo: the intent structs derive `schemars::JsonSchema` but `desktop/Cargo.toml` has no `schemars`; `gpui-pre` depends on schemars 1.x (`gpui-pre-0.3.10/Cargo.toml:467-469`) and the lockfile has 1.2.3 (`desktop/Cargo.lock:5542-5544`). Add `schemars = "=1.2.3"` (the repo pins exactly). Or use `#[action(no_json)]` (`gpui/action.rs:78-81`) and skip schemars: smaller. Without a namespace the names are bare `OpenFile` etc. and can collide with others (duplicate names panic, `gpui/action.rs:54`); `namespace = intents` is one attribute.
2. Duplicate global handlers are silent: in a window dispatch the bubble loop sets `propagate_event = false` before each listener and breaks after the first, in reverse order (`gpui/window.rs:6619-6636` (`propagate_event = false` at :6625)), so only the newest `cx.on_action::<OpenFile>` runs and an older owner is shadowed with no error. `App::dispatch_global_action` instead runs all (`gpui/app.rs:2975-3012`). The kit does not enforce one owner; our own registration check is required (test 12).
3. `CommandItem` hides what we need: `label` is private and `action` is `pub(crate)` (`component/command/item.rs:9-19`); only `matches` and `label_text` read them and both are `pub(crate)` (`:114, :130`). The registry cannot read an item back to check duplicates or for tests, so it must store id and label itself next to the item. The stub's `items(cx) -> &[CommandItem]` (`command_registry.rs:18`) is not enough on its own.
4. Search is weaker than the old one: the kit does one case-insensitive substring of the whole query against label and keywords (`item.rs:114-128`), no per-word match and no `detail` field. Old: all words, any order, label plus detail (`commandRegistry.ts:116-129`). Typing "settings open" would find "Open settings" before and not now. Putting the detail line into `.keywords` restores matching of the detail, not word order; showing a detail line needs `.child(..)`, which disables the kit's row reuse (`item.rs:141-143`).
5. "Disabled" is a plain bool fixed when the item is built (`item.rs:169-174`); the old one was a closure re-run at search time (`commandRegistry.ts:28, 98-105`). Fine if the palette rebuilds items each open (as the old one did), but the registry must keep closures, not built items.
6. `KeyBinding::new("secondary-k", ..)` panics: `new` parses with no OS, and `secondary` needs one (`gpui/platform/keystroke.rs:158-162` with `binding.rs:35-46`). Use `cfg!(target_os = "macos")` to pick `cmd-k` or `ctrl-k`, or `KeyBinding::load` with `cx`.
7. `cx.on_action` handlers have no `Window` (`gpui/app.rs:2760-2764`); opening the dialog needs one. Go through `cx.active_window()` then `window.update(..)`, or put the handler on the root view.
8. Dispatch is deferred (`gpui/window.rs:2565-2570`): a command's handler runs after the current event, while the palette dialog is still open. Close the palette from `on_confirm` (which runs after the action, `state.rs:617-638`) or `on_close`, and make `overlay_closed` fire once whichever of Enter, Escape or backdrop got there first (test 9).
9. A key binding for a user-defined Run action (later lane) cannot be removed one at a time: the only removal is `clear_key_bindings` (`gpui/app.rs:2747`). Note for the run lane, not g0-host now.
10. An intent sent before its owner registered is dropped; the old app parked file and URL requests (`openFileBus.ts:56-58`, `workbenchNavigation.ts:321`). Startup order in `main.rs` must register owners before the launch link is handled (old order: `routes/+page.svelte:420-445` waits for the first session).
11. The intent payloads are thinner than the old requests. Old open-diff carries `projectRoot` + `relativePath` (`workbenchNavigation.ts:59-64`), new `OpenDiff` only `path`. Old open-file carries `projectRoot`, `readOnly`, `column`, `preview`, `pin` (`openFileBus.ts:15-31`); new has `path` and `line`. `readOnly` matters: another project's file opens read-only (`GitDiffView.svelte:178-185`, `openFileBus.ts:106-110`). Per KISS, add fields only when the owning lane needs them, but Q4 should be answered first.
12. Open-URL redirects (Notion, GitHub PR, `assembly:`) lived in the sender-side helper (`workbenchNavigation.ts:303-323`). If the browser feature owns `OpenUrl`, it cannot send to the PR or Notion features (a feature never uses another's folder, `desktop/README.md` Layout). The translating step belongs in host (the launcher) or each owner must re-dispatch a different intent.
13. Guard: `desktop/scripts/guard.sh` forbids file reads in a file that draws and timers outside `core/clock/`; `PaletteView` must not read files or wait on time. The Dialog's own `defer_close_dialog` timer lives inside the kit, not in our code.

## 7. Open questions for Jesse

- Q1. Which palette commands carry over? The old palette really offered only Reset layout, Show bottom dock and Open settings; the 22 in `shellCommands.ts` were never hooked up. Default proposed: each lane registers its own "show X" command when its panel exists; g0-host ships only Open palette, Reset layout, Show bottom dock, Quit (the others as their lanes land).
- Q2. Shortcuts: Cmd/Ctrl+K is certain. Keep Cmd/Ctrl+Shift+P too (the kit allows several bindings per action)? Is Cmd+Q a "must" (yes by default on macOS)? Cmd+P quick open was never wired; skip? `docs/port-product-decisions.md` (43 lines) says nothing on palette, shortcuts or commands, so none of this is decided yet.
- Q3. Two commands with the same id: fail loudly at start (proposed, matches the intent rule) or first one wins as the old registry did (`commandRegistry.ts:21`)?
- Q4. Intent data: is `OpenDiff{path}` enough (one project open at a time), and may files from another project open read-only (needs a `read_only` flag on `OpenFile`)?
- Q5. Is "no owner yet" for an intent an error to show, or silent? (Old app: silent, parked for file and URL.)
- Q6. The palette lists a command's key beside its label (kit does this for free). Settled as yes unless Jesse objects; the old palette showed only a `Cmd+K` hint at the top.

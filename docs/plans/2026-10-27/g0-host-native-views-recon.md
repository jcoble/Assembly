# g0-host: native views, recon for the code step

Read-only recon, 2026-10-27. Nothing was built or run (this machine is Linux with no window). Every claim has a
file:line or commit id. "Verified" means I read that line. Paths: `REG` = `~/.cargo/registry/src/index.crates.io-1949cf8c6b5b557f`,
`OLD` = `~/dev/work/mac-command-bar` (commits) / `OLD/tauri-svelte-preview/src` (files).

Behaviors this serves: `docs/behavior/g0-host.md` g0-host#5 to #10. `desktop/src/core/native_views/` exists and is empty
(verified, `ls`). The spike folder `~/dev/work/assembly-gpui-spike` has no mention of webview, ghostty or native views
(grep over `*.md *.toml *.rs`, only hit is an unrelated word in `backend/src/dotnet/README.md:41`).

## 1. What to port

The old app's views were Tauri child webviews (the browser, `src-tauri/src/browser.rs`) and a Ghostty that ran as
WASM inside the page (`ghostty-web`, `OLD/.../terminal/ghosttyFactory.ts:1-12`, `package.json:197`). So the old terminal
was never a native view; the GPUI one (Ghostty's own Metal view) is new work with nothing to copy. Only the browser's
native-view fixes carry over literally.

| Commit | What it did | Bug it prevented | In GPUI terms |
|---|---|---|---|
| 821ec040 (2026-08-09) | Restore only saved the URL; `activateBrowser()` made the tab when the panel opened (`browserStore.svelte.ts`). | The last page floated over the whole app at launch, because a native view shows itself the moment it is created. | A native view is created hidden and shown only after the placement rule has said it may be (g0-host#5). Session restore must not create a view; the pane that is in front asks for it. |
| f127df9a (2026-09-01) | Dock panels got `renderer: 'onlyWhenVisible'` (`ShellFrame.svelte`), plus listener cleanup. | Hidden panels kept their surfaces alive. | A panel that is not the front tab is not drawn by the dock, so its host element is not prepainted; the module treats "host not seen this frame" as hidden and, after the tab is gone, as releasable (g0-host#9). |
| ded79246 (2026-10-06) | Popup watcher (a MutationObserver on the whole page) ran only while the browser tab was in front; it set `popupOpen`, which fed the browser panel's `visible` (`routes/+page.svelte:157-159,333-350,779`). | Menus, lists, cards and dialogs drawn under the native page. | The overlay signals in section 2(a) feed one "overlay open" flag. Note the old rule: it hid the page only when a popup's rectangle overlapped the page (`popupCoversPage`, `browserPanelBounds.ts:78-89`, commit 85cf041a8), and left tooltips out on purpose (`+page.svelte:154-155`). See open question 1. |
| b0845e27 (2026-09-04) | `hostRectFitsPanel` rejected a measured rectangle that left its region or overlapped the center; closing the right panel hid the whole browser first, because closing tabs is asynchronous (`browserPanelBounds.ts`, `browserStore.svelte.ts`). | During a layout change the dock briefly moved the host, and the native view was placed over the whole shell; a closing panel left an invisible view eating clicks. | Clip every placement to the pane's content bounds; reject a rectangle that is empty or outside the window. On pane or tab close, hide first and release second, in that order, in the same pass. |
| c65922f6 (2026-08-14) | `content_inset`: asked the NSWindow for frame height minus `contentLayoutRect` top and added it to every rectangle (`browser.rs`, `content_inset`, `into_window_space`). The view was created hidden, kept hidden until its first measured placement (`placed`, `pending_show`), and a `show` before then was remembered. | The page was drawn one title bar too high, covering the panel's own address row; a restored tab flashed in the middle of the shell. | GPUI bounds are relative to the window's content area. wry places a child view relative to its parent view and handles flipping (`REG/wry-0.57.0/src/wkwebview/mod.rs:1031-1050,1440-1456`), so the inset may not be needed. Unverified; Mac check 3 below. Keep "created hidden, shown after first placement". |
| 893cc7dd (2026-09-18) | Expanded browser aligned to the workspace; moved a node to `document.body`; re-measured on the next animation frame (`BrowserPanel.svelte` `fillWindow`). | Stale bounds after a pane moved to another parent. | The frame wait is not allowed here (`desktop/scripts/guard.sh:15`). Not needed: placement runs in every painted frame (section 2(c)), so a moved pane is measured by the next paint. |
| ba5a652f (2026-07-28) | Sessions-column CSS: `contain: paint`, panes absolutely filled, stack laid out at its real height (`ShellSidebar.svelte`). | A strip painted outside its column. This is a CSS fix, not a native-view fix. | The same idea in GPUI is clipping to the pane: intersect the host bounds with the content mask (section 2(c)). |
| 4f61e404 (2026-08-22) | Released every non-active terminal view on session change and on show, keeping the PTY mapping so scrollback came back from the backend (`terminalService.ts`, `dropView`, `releaseView`, `show`). | Hidden terminal views piling up. | Matches g0-host#9 and terminal#5/#9 in `terminal` worktree `docs/behavior/terminal.md`: the backend keeps the process and the last 4 MB; the module drops the Ghostty view for a tab that is not in front and rebuilds it from the backend on return. |
| 34223b94 (2026-10-22) | Terminal tab keyed on the saved theme, so a theme change remakes it (`RightPanel.svelte`, `ghosttyFactory.ts`); the palette is read from the saved theme, not the theme service's current one. | Old canvas colors after a theme switch. | Include the theme id in the terminal view's identity. The placement pass compares the id it built with to the current one and remakes on difference; no listener needed. Read the id from the setting's owner, not from whichever theme global changed last (start-up order, same as the old note). |

`terminalService.ts` around line 647 (it lives at `OLD/.../shell/terminal/terminalService.ts:640-660`, not `shell/terminalService.ts`):
the terminal manager auto-shows only the first view and `showView` hides only views that already exist, so a view
created while another is active was never hidden. `ensureViewFor` hides any view that is not the active key the moment
it exists (`:647-658`). GPUI rule: a new view starts hidden and the pass shows it, never the other way round.

## 2. Kit hooks found (all verified by reading)

Which layer: `gpui-kit` defaults to `component` on, `assets` on (`REG/gpui-kit-0.8.1/Cargo.toml:39-47`), and
`desktop/Cargo.toml` takes defaults. `gpui-component` is a skin over `gpui-base`; the hooks below are in base unless said.
`desktop/Cargo.lock` lists `gpui-pre-macos` (`Cargo.lock:2368`) but its source is not in the registry on this machine, so
nothing macOS-specific in GPUI could be read.

### (a) Knowing when overlays open and close

No single "an overlay is open" signal exists. Pieces:

- Dialogs and sheets: `WindowExt::has_active_dialog` (`gpui-component/src/window_ext.rs:109`, impl `:196-201`, ignores
  dialogs already closing) and `has_active_sheet` (`:35`, impl `:164`). Both take `&mut Window` and read the window's
  `WindowState`. That type is `pub(crate)` (`root.rs:25`), so the app cannot watch it, only ask. The state's `cx.notify()`
  on open/close (`root.rs:375-445`) re-renders the window through the Root, which observes every plugin
  (`gpui-base/src/root.rs:140-153`), so a question asked every painted frame sees the change.
- Notifications (toasts): `WindowExt::notifications` (`window_ext.rs:131`, impl `:252`) returns the live list.
- Popovers, dropdown menus, select, combobox: they hold a registration while open. `GlobalState::is_in_deferred_context(cx)`
  (`gpui-base/src/global_state.rs:144-150`) is true while any is open. Registered at `gpui-base/src/popover.rs:83`,
  `gpui-component/src/select.rs:464`, `combobox.rs:538`. Dropdown menus are built on `Popover`
  (`menu/dropdown_menu.rs:102-118`); the kit's own tests assert the release (`:388-400`). The token is dropped with the
  state, so a popover scrolled away cannot leave it stuck (`global_state.rs:152-162`).
- Per-component callbacks exist where a registration is not enough: `on_open_change` on base `Popover`
  (`gpui-base/src/popover.rs:284`), component `Popover` (`popover.rs:225`), `DropdownMenu` (`dropdown_menu.rs:72`),
  `HoverCard` (`hover_card.rs:114`), `Combobox` (`combobox.rs:91`), `ColorPicker` (`color_picker.rs:521`), date picker
  (`gpui-base/src/date_picker.rs:28`).
- Gaps (no kit hook found):
  - Context menu: no open callback and no registration. It builds its `PopupMenu` in a deferred call when opened
    (`menu/context_menu.rs:454-460`) and closes on `DismissEvent` (`:515`, emitted at `popup_menu.rs:1076`). The app's
    menu builder closure runs at open, so it can take a token there and let the menu entity own it; whether the entity
    drops on dismiss is not verified.
  - Tooltips: the overlay is `pub(crate)` in `WindowState` (`root.rs:31`, accessor `:605` also `pub(crate)`); no accessor. GPUI's own tooltip is a separate
    root drawn after deferred draws (`gpui-pre/src/window.rs:3748-3776`).
  - Hover cards do not register in `is_in_deferred_context` (no `register_deferred_popover` in `hover_card.rs`).
  - The Cmd-K palette is the app's own (`desktop/src/features/palette/` is empty); it can take a token itself.
  - The macOS native menu bar needs nothing: it is not GPUI-drawn.
- Dialog closing: `has_active_dialog` goes false when exit begins (`root.rs:436-441`, `window_ext.rs:196-201`), while the
  dialog's fade-out runs on the kit's timer (`root.rs:473`). The view would reappear under a fading dialog.

### (b) Drag start and end, divider resize

- `DockEvent` has only `LayoutChanged` and `DragDrop` (`gpui-base/src/dock/dock_area.rs:39-45`); `TabGroupEvent` has Drop,
  DragDrop, ClosePanel, ActiveChanged, ZoomIn/Out (`dock/tab_group.rs:31-51`). No drag-start event. `DockToggleEvent`
  (`gpui-component/src/dock/mod.rs:87`) is only for dock open/close by the user. `ResizablePanelEvent::Resized`
  (`gpui-base/src/resizable/panel.rs:14`) fires on drag end only (`resizable/mod.rs:277-279`); `resizing_panel_ix` is private.
- All of these drags are ordinary GPUI drags: tab drag (`gpui-component/src/dock/tab_panel.rs:417,597`), splitter handle
  (`gpui-base/src/resizable/panel.rs:403`), dock edge handle (`gpui-component/src/dock/dock.rs:142`, `resize_to` `:229`,
  `end_resize` `:241`). GPUI stores the drag in the app and refreshes the window when it starts
  (`gpui-pre/src/elements/div.rs:3061-3067`) and when the mouse is released (`gpui-pre/src/window.rs:6016-6023`).
  So `cx.has_active_drag()` (`gpui-pre/src/app.rs:3023`) is a plain state read that is true from the first moved frame to
  the first frame after release. It also turns true for an OS file drop entering the window (`window.rs:5700-5710`),
  which is wanted. No observer or event needed.
- Panel active signal: `Panel::set_active` (`gpui-base/src/dock/panel.rs:53`) is delivered on the next tick
  (`dock/tab_group.rs:472-486`, `cx.spawn_in`), `on_removed` on close (`panel.rs:82`), `visible` (`panel.rs:28`). Too
  late alone for "hide on tab switch": there is a frame where the new tab is drawn and the old view is not yet told.

### (c) Laid-out bounds each frame, without frame waits

- `ElementExt::on_prepaint` (`gpui-base/src/element_ext.rs:11-30`) adds an absolute full-size canvas child and calls back
  with the resolved bounds every time that element is prepainted. Underneath is `canvas(prepaint, paint)`
  (`gpui-pre/src/elements/canvas.rs:10-19`, bounds passed at `:62-70`).
- Frame order is fixed: all prepaint (root, deferred popups, drag preview, tooltip) happens, then all paint
  (`gpui-pre/src/window.rs:3728-3776`). So a canvas `paint` callback near the root runs after every host has reported
  its bounds and after every popup has been prepainted: one place to run a single placement pass.
- Clipping: `Window::content_mask()` (`window.rs:4329`, prepaint/paint only) is the visible region from ancestors'
  overflow; `ContentMask::intersect` (`:2257`). Use it, not the raw bounds, to clip to the pane.
- Window facts if wanted: `Window::bounds` (`window.rs:2875`), `scale_factor` (`:3108`), `is_window_active` (`:2990`).
  `DockArea::bounds` records the area each frame (`dock_area.rs:179`).
- The guard bans `on_next_frame`, `request_animation_frame` and timers outside `core/clock/` (`desktop/scripts/guard.sh:15`)
  and sleeping anywhere (`:22`). Canvas callbacks are not on that list.

### (d) Attaching a native view to the window

- `gpui::Window` implements `HasWindowHandle` and `HasDisplayHandle` (`gpui-pre/src/window.rs:7616-7627`), from the
  platform window (`platform.rs:1224`). The test window returns `NotSupported` (`window.rs:8621-8635`), so unit tests
  cannot create a view. `Window` also has its own `window_handle()` returning GPUI's handle (`app.rs:3037`); call the trait
  one fully qualified.
- `wry` has `WebViewBuilder::build_as_child<W: HasWindowHandle>` (`REG/wry-0.57.0/src/lib.rs:1571`), `set_bounds`
  (`:2263`), `set_visible` (`:2268`). On macOS the child's frame is set against its parent view with flipping handled
  (`wkwebview/mod.rs:1031-1050,1440-1456`). wry 0.55.1 and 0.57.0 are in the registry only because of Tauri; they are not
  in `desktop/Cargo.toml` or `Cargo.lock`.
- No crate named `gpui-webview`, `gpui_webview` or any Ghostty/libghostty binding is in the registry (`ls REG | grep`
  gives only `gpui-*`, `wry`, `objc2-*`, `raw-window-handle-0.6.2`). Searching the registry index for `gpui-webview` found
  nothing locally; I did not query crates.io (no network use asked). Ghostty's Metal view has no binding at all here.
- `objc2-app-kit 0.3.2` and `objc2-web-kit 0.3.2` are in the registry (so a raw `WKWebView` is possible without wry);
  nothing uses them yet.

## 3. Proposed tests

Design the module around one pure function so the rule is testable on Linux:

`placement(inputs) -> Placement` where inputs = the view's host bounds and content mask (or none if the host was not
prepainted this frame), is-front-tab, overlay-open, drag-active, window size, and the view's last shown state; output =
Hidden | Shown(bounds) | Release. Plus a small registry that decides create/remake/release.

Unit tests on Linux (`cargo test --locked`, no window):
1. Host not seen this frame: Hidden. Seen after a tab switch: the old view Hidden, the new one Shown only after its first
   placement (821ec040, c65922f6 "created hidden").
2. A new view is Hidden until the first placement even if "show" was asked first.
3. Overlay open (each of: dialog, sheet, popover token, palette token, context-menu token, notification): Hidden; all
   closed: Shown at the same bounds. Two overlays open, one closes: still Hidden (counts, not a bool).
4. Drag active: Hidden. Drag ends: Shown at the host's new bounds, not the old ones.
5. Bounds clipped to the content mask; mask smaller than bounds gives the intersection; empty or zero-size result gives Hidden
   (b0845e27's `hostRectFitsPanel`, ported as "empty or outside window is rejected").
6. Bounds below the tab bar: host bounds start at the pane body's top (feed a layout where the host sits under a 30 px bar;
   the output top equals the body top).
7. Pane moves or resizes: next pass returns the new bounds; unchanged bounds return "no change" (no native call).
8. Tab closes: Hidden then Release in the same pass, never Release while Shown.
9. Theme id changes: the terminal entry is remade; the browser entry is not.
10. Inactive terminal (not front for more than the front tab): released, and re-created from the backend on return
    (4f61e404); a browser is kept hidden, not released (assumed, see question 4).
11. Guard still passes: no timer, frame wait, sleep, or socket in `core/native_views/` (`bash scripts/guard.sh`).

Mac window checks (run on the Mac through the spike thread; Workbox has no window):
1. Launch with a saved terminal tab and browser tab in the layout: neither paints over the other pane at start.
2. Open Cmd-K over a terminal tab, then a browser tab, then Settings dialog, a dropdown, a select, a context menu, a
   toast: the view is out of the way each time and returns on close. Hover a tooltip near the pane edge.
3. Window with the standard title bar: the browser's top edge sits under the pane's tab bar, not one title-bar too high
   (c65922f6). Repeat with the window fullscreen and on a second display with a different scale factor.
4. Drag a tab across panes; drag a splitter; drag a dock edge. View hidden during, placed in the new pane after release.
   Release the mouse outside the window and over the native view.
5. Switch tabs rapidly 20 times: no frame with both views visible (watch for the `set_active` delay, section 2(b)).
6. Close a tab: process or page gone, no leftover window, memory falls (Activity Monitor), no invisible area eating clicks
   (b0845e27).
7. Switch theme: terminal colors follow; scrollback is back.
8. Scroll the pane's content so the host is partly clipped by an ancestor: the view is clipped, not painted outside.

## 4. Risks

- The `set_active` delay (section 2(b)) means "hide on tab switch" cannot use it alone. Proposed fix: hide whenever the host
  was not prepainted in the current frame. That needs the placement pass to run after prepaint (section 2(c)), once per
  frame, so a root-level canvas `paint` callback is the one place that changes native views. Not tried; it is a design,
  not a measurement.
- Native calls from inside a paint callback: allowed by GPUI's types, but a WKWebView `setFrame`/`setHidden` during draw
  might force a layout inside layout. Unverified. Check on the Mac.
- A frame must happen for the pass to run. Overlay open, drag start and drag end each refresh the window (sections 2(a),
  2(b)); the context-menu and tooltip gaps do not have a hook that guarantees it.
- Dialog fade-out shows the view early (end of 2(a)). Tooltips and hover cards cannot be detected; a tooltip near a pane
  edge may paint under a native view.
- A native view eats mouse events inside its rectangle. A drag that ends over it never reaches GPUI, so a stuck drag is
  possible if the view is shown during a drag. The hide-on-drag rule is what prevents it; test 4 covers it.
- Ghostty: nothing exists to bind to; the Metal view needs its own work (build of the library, a Rust binding, input
  routing, resizing, font and color settings). That is bigger than the module and should be its own task.
- macOS GPUI source (`gpui-pre-macos`) could not be read here, so: whether the content view is flipped, whether
  `Window` hands out the content `NSView` or the window, and window-hit-test forwarding (`gpui-base root.rs` has
  `install_window_hit_test_forwarder` under `target_os = "macos"`, `:176-177`) are unknown. The forwarder may interact with
  child views.
- Test windows return no raw handle (`window.rs:8621`), so the native layer must sit behind a small trait that a fake
  implements for tests. That is a single seam, not a new abstraction layer (flag if the diff owner disagrees).
- Persisting a hidden browser means a live WKWebView per kept tab. Memory grows with tabs unless inactive ones are released.
- The kit pins: `=0.8.1` and the Cargo.lock; a kit bump can change any hook above (all are `pub` today except tooltips and
  the dialog state type).

## 5. Open questions for Jesse

1. Hide on any open overlay, or only when the overlay overlaps the view? The old app hid only on overlap and ignored
   tooltips (`+page.svelte:154-155`, `popupCoversPage`). The kit gives no popup rectangles, so "any overlay" is the
   simple version; overlap would need each popup to report bounds. Which?
2. Tooltips and hover cards over a native view: accept that they paint under it near a pane edge, or hide the view while
   the pointer is over the pane's tab bar?
3. Dialog fade-out: accept the view returning a moment early, or keep it hidden until the kit's exit timer finishes (needs
   our own dialog wrapper to hold a token)?
4. Which tabs are released and which are only hidden? Proposal: terminals released when not in front (backend keeps 4 MB,
   terminal#5); browsers hidden but kept (so a page does not reload), released when the tab closes. Is losing page state
   on release ever acceptable?
5. Webview route: use `wry` directly (it is in the registry and takes the GPUI window handle) or wait for a `gpui-webview`
   crate? I could not find one. Using `wry` adds a dependency; the pin and the Mac build need your OK.
6. Ghostty: one task for the native Metal terminal, separate from this module? Until it lands, the module can be built and
   tested with the browser and a fake view.
7. Mac time: who runs the Mac checks (section 3), and is a still image of the view (old behavior note) wanted while it is
   hidden, or is a blank pane fine?

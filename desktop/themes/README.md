# Themes

Each file here is one gpui-kit theme (gpui-component 0.8.1's `ThemeSet` with one theme in it). The setting is one
theme id, the file's stem; `src/core/theme/theme_id.rs` lists the ids, and an id that isn't one of them gives
`assembly`. There is no light/dark toggle: the mode follows the theme picked (`mode` in its file), so the light themes
put the window in light mode and the rest in dark.

The kit watches this folder and re-reads a changed file, then re-applies the theme in force. That works only while
no file says `is_default: true` (the kit skips a reloaded default theme, registry.rs:274-287), so every file says
false.

## Roster

| Id | File | Kit name | Comes from |
| --- | --- | --- | --- |
| assembly | assembly.json | Assembly | The design system Assembly, the approved look (Jesse, 2026-10-27 19:46Z; version 1793120046-6eb7) |
| assembly-glass | assembly-glass.json | Assembly Glass | New: Assembly with a blurred window behind translucent grounds (macOS only) |
| assembly-light | assembly-light.json | Assembly Light | New: the design system's structure on white, a full light theme |
| houston | houston.json | Houston | mac-command-bar themeRegistry.ts HOUSTON |
| dracula | dracula.json | Dracula | themeRegistry.ts DRACULA |
| tokyo-night | tokyo-night.json | Tokyo Night | themeRegistry.ts TOKYO_NIGHT |
| graphite | graphite.json | Graphite | themeRegistry.ts GRAPHITE |
| amy, cobalt | `<id>.json` | Amy, Cobalt | themeRegistry.ts THEMEMIRROR_THEMES: the Assembly shell with thememirror 2.0.1's editor and syntax colours |
| rose-pine-dawn | rose-pine-dawn.json | Rosé Pine Dawn | New, a full light theme: @rose-pine/palette 4.0.1 `dawn`, with thememirror 2.0.1's syntax |
| ayu-light | ayu-light.json | Ayu Light | New, a full light theme: ayu 9.1.0 `light`, with thememirror 2.0.1's syntax |

Off macOS, `assembly-glass` resolves to `assembly`, since Linux has no reliable blur.

## What every theme sets

- `font.size` 13 (body), `mono_font.size` 12 (the design system's `code`, 12/20), `radius` 8 (`radius-sm`),
  `radius.lg` 16 (`radius-lg`, which the kit uses for dialogs and notifications), `shadow` false. The fonts stay
  unset, which is the system face and system monospace. The radii are the same in every theme, since shape is the
  app's, not a theme's.
- The kit has only two radii. The design system's `radius-xs` 6 (tab chips, tree rows, tags), `radius-md` 12 (cards,
  glass menus; the kit's command palette takes a radius of its own), the work panel's `radius-lg`, `radius-xl` 20
  (the composer, the user's bubble) and `radius-full` (pills) are set by the
  components that draw those (g0-shape and the view lanes).
- `tab_bar.background` is clear in every theme: the frame's work panel is rounded and GPUI can't clip children to
  rounded corners, so a filled tab strip would square them off (g0-shape).
- Motion: theme files can't set it. The kit's `cx.theme().motion` (gpui-component theme/motion.rs) is the one place
  animation timing lives, and every lane's animations read it instead of writing their own durations (Jesse,
  2026-10-27: "small animations wherever we can"): `duration_fast` (120 ms) for hover fills and fades,
  `duration_normal` (180 ms) for menus, popovers, the drawer and panes moving, `easing_enter`, `easing_exit` and
  `easing_move` for their curves, and `spring_control` and `spring_move` for anything dragged or springing. The
  kit's slow step and move spring are 280 ms (motion.rs:28, 36; tab indicators use the spring, tab/tab_bar.rs:249),
  past the design system's 180, so `set` brings both down to 180 ms. Reduced motion is the kit's too: its components
  already settle at rest when the system asks for it.
  The one motion token that isn't the kit's is grow in place's timing, `GROW` 160 ms on `easing_enter` with the
  content fading in from `FADE_AFTER` 40 ms in, following the height (`faded`, src/core/theme/grow.rs): every row
  that grows open reads it from there.
- A theme switch fades: the window root draws the old theme's `background` over everything and fades it to clear over
  `duration_normal` on `easing_enter` (src/core/theme/fade.rs). There's no fade when the window first opens, when the
  theme in force is set again, or when the new theme has the same `background`.
- Kit 0.8.1 has no highlight keys for added, removed and changed lines (highlighter/registry.rs:318-349), so the
  design system's `add-bg`, `del-bg`, `mint` and `amber` for diffs belong to the diff view that draws them, not to
  these files.

## Kit key from design-system role

These are the keys in assembly.json, matched to the design system's tokens by value. The approved look kept the role
names and changed their values (the design system Assembly, version 1793120046-6eb7), so assembly.json, assembly-glass.json and assembly-light.json
are this table with the new values, apart from the exceptions listed under it.

| Design-system role | Kit keys |
| --- | --- |
| surface | `background`, `accordion.background`, `list.background`, `list.even.background`, `list.head.background`, `table.background`, highlight `editor.background` (and, before the approved look, `tab_bar.background` and `title_bar.background`) |
| rail | `sidebar.background`, `status_bar.background` |
| field | `accent.background`, `list.hover.background`, `tab_bar.segmented.background`, `skeleton.background`, `description_list.label.background`, highlight `editor.active_line.background` |
| raised | `group_box.background`, `popover.background` |
| chip | `muted.background`, `secondary.background` |
| selected | `list.active.background`, `list.active.border`, `tab.active.background`, `secondary.hover.background`, `sidebar.accent.background` |
| selected-strong | `secondary.active.background`, `switch.background` |
| line | `border`, `input.border`, `sidebar.border`, `title_bar.border`, `status_bar.border`, `window.border` |
| line-soft | `table.row.border`, `chart.grid` |
| ink | `accent.foreground`, `link.hover`, `primary.hover.background`, `sidebar.accent.foreground`, `tab.active.foreground`, `slider.thumb.background`, highlight `editor.active_line_number` |
| ink-body | `foreground`, `group_box.foreground`, `popover.foreground`, `secondary.foreground`, highlight `editor.foreground` |
| ink-2 | `primary.active.background` |
| ink-muted | `muted.foreground`, `sidebar.foreground`, `table.head.foreground`, `description_list.label.foreground` |
| ink-faint | highlight `editor.line_number` |
| ink-tab | `tab.foreground` |
| primary | `primary.background`, `sidebar.primary.background` |
| on-primary | `primary.foreground`, `sidebar.primary.foreground`, and the foreground of `danger`, `success`, `warning`, `info` |
| accent | `caret`, `drag.border`, `link`, `link.active`, `progress.bar.background`, `slider.background`, `chart.1` |
| focus-line | `ring` |
| red | `danger.background`, `base.red`, `chart.bearish`, highlight `error.border` |
| mint | `success.background`, `base.green`, `chart.bullish` |
| amber | `warning.background`, `base.yellow` |
| blue | `info.background`, `base.blue` |
| lavender | `base.magenta`, highlight `hint` |
| aqua | `base.cyan` |
| (no role; the spike's picks) | `drop_target.background`, `scrollbar.*`, `selection.background`, `overlay`, `tab.background` |
| aqua, lavender, mint, amber | `chart.2`, `chart.3`, `chart.4`, `chart.5` |
| the same role as the key without `.light` | `base.*.light` |

Exceptions in the approved look (Assembly, Assembly Glass, Assembly Light):

- `title_bar.background` takes `rail`: the title row sits on the rail with the sessions column.
- `tab_bar.background` is clear, as in every theme (see "What every theme sets").
- `link`, `link.active` and highlight `link_text` take `aqua` ("aqua marks links and references").
- `primary.hover.background` takes `ink-body`: `primary` is `ink` now, so `ink` as hover would show no change.
- `list.active.background` and `list.active.border` are white at 0x1a (black at 0x1a in Light), the alpha that lands
  on `selected` over `surface`, because the kit caps that key at 20% opacity (schema.rs:1025-1049).
- `popover.background` is opaque: #222222, what `glass` (rgba(40,40,40,.74)) looks like over `surface`, and #ffffff in
  Light. The design system puts menus and popovers on glass behind a 20 px blur, but GPUI 0.3.10 can blur only the
  whole window, not what's behind one element, so translucent menus would show the text under them. The kit draws the
  popover's ring from `foreground` at 10% (styled.rs:190-193), so `glass-edge` has no key.
- `overlay` is black at 0x99 in the dark two (0x33 in Light). The kit draws a dialog, such as the Cmd-K palette, on
  `background` with a `border` ring and a 10% black shadow (dialog.rs:779-782, 343-357), and none of that shows on
  near-black grey. A deeper dim is the one theme value that makes the ring read without changing `line` everywhere.
- The status-pill, diff, `code-bg`, `ink-icon` and provider tokens have no kit key. They stay in the design system
  until a view needs them per theme.
- Houston, Dracula, Tokyo Night, Graphite, Rosé Pine Dawn and Ayu Light keep their own colours; only the radii and the
  clear tab strip changed in them. Amy and Cobalt take the new Assembly shell, as before.

## The four old themes (Houston, Dracula, Tokyo Night, Graphite)

Each role takes the old theme's colour from themeRegistry.ts, by the 2026-10-26 table
(docs/plans/2026-10-26/implementation-plan.md, "The design system's roles on the old names"):

| Role | Old name |
| --- | --- |
| surface | `--color-surface` (not `--color-bg`: the design system has one ground) |
| rail | `--color-rail` |
| field | `--color-elevated` (also `--color-hover`) |
| raised, chip | `--color-elevated` (nearest existing colour) |
| selected | `--color-selected` |
| selected-strong | `--color-selected` (nearest) |
| line | `--color-border` |
| line-soft | `--color-border` (nearest) |
| ink, ink-body, ink-2 | `--color-text` (ink-body and ink-2 are nearest) |
| ink-muted | `--color-text-2` |
| ink-faint | `--color-text-3` |
| ink-tab | `--color-tab-unfocused-text` |
| primary | `--color-text` (the old themes have no solid fill; nearest light colour) |
| on-primary | `--color-on-accent` |
| accent | `--color-accent` |
| focus-line | `--color-focus` |
| mint, blue, amber, red | `--color-good`, `--color-live`, `--color-attention`, `--color-bad` |
| lavender, aqua | the theme's terminal magenta and cyan (terminalPalettes.ts) |
| `selection.background` | `--color-text-selection` |
| `overlay` | `--color-scrim` |
| `drop_target.background`, `scrollbar.*`, `tab.background` | the same as Assembly |

Their editor syntax colours: Dracula takes thememirror's own Dracula syntax (codeMirrorTheme.ts:112). Houston,
Tokyo Night and Graphite take the base syntax colours the old editor used for all of them (codeMirrorTheme.ts:8-20).

## The ThemeMirror entries (Amy, Cobalt)

Each is Assembly's file with its own editor from thememirror 2.0.1: its syntax colours, and its editor background,
active-line ground, text and line-number colours from the theme's `settings`. The kit has no theme inheritance, so
Assembly's `colors` are copied into these files; that is the one place the colours repeat. In the old app each entry
brought its own editor background, and without it their code reads at about 2-3:1 on our dark ground (Jesse,
2026-10-27: own editor backgrounds, replacing the 10-26 "syntax colours only"). Not from thememirror: `hint` takes the
theme's comment colour and the Markdown keys take its foreground, keyword and string, so they read on its own editor.

The old app's other three entries, Ayu Light, Rose Pine Dawn and Tomorrow, were light editors in the dark shell and
aren't ported that way (Jesse, 2026-10-27); Ayu Light and Rosé Pine Dawn come back as full light themes, below. Kit
0.8.1's code editor draws text and the current line's number in the app-wide `foreground` and other line numbers in
`muted.foreground`, and never reads `highlight.editor.foreground` or the line-number keys (gpui-component
input/input.rs:540-541, gpui-base input/base/element.rs:3117, 3125), so on a white editor in a dark shell its text is
light gray on white. (The `editor.foreground` and line-number keys in the files here are unread for the same reason; on
their dark editors the app-wide colours read fine.)

## The light themes (Rosé Pine Dawn, Ayu Light)

Jesse, 2026-10-27: "we do need some light themes". Each is a whole light window, not a light editor in the dark
shell, so the kit's code editor, which draws text in the app-wide `foreground`, reads (see above). Every kit key takes
its role from the table above, and each role takes the upstream palette's colour:

| Role | Rosé Pine Dawn | Ayu Light |
| --- | --- | --- |
| surface (also the editor) | base | editor.bg (surface.lift) |
| rail | surface | ui.bg (surface.base) |
| field | overlay | ui.selection.normal |
| raised | surface | ui.popup.bg (surface.over) |
| chip | overlay | surface.sunk |
| selected | highlightMed alpha (the kit caps `list.active.background` at 20%, so an opaque pick draws fainter than hover) | ui.selection.active |
| selected-strong | highlightHigh | ui.line at 0x33 (no upstream colour) |
| line, line-soft | highlightMed, highlightLow | ui.line, editor.line |
| ink, ink-body, primary | text | editor.fg |
| ink-2, ink-muted, ink-tab | subtle | ui.fg |
| ink-faint | muted | editor.lineNumber.active |
| on-primary | base | surface.over |
| accent, focus-line (at 0x59) | pine | common.accent.tint |
| red, mint, amber, blue, lavender, aqua | love, pine, gold, foam, iris, rose (Rosé Pine's terminal colours) | common.error, vcs.added, yellow l4, blue l2, purple l2, teal l3 |

Not by role: `selection.background` is the accent at 0x4c for Rosé Pine and editor.selection.active for Ayu;
`scrollbar.thumb.*` is the ink at 0x24 and 0x3d; `overlay` is black at 0x33. Ayu's accent is a fill colour (upstream
gives it its own `on` colour) and reads at 2.2:1 as text, so Ayu's `link` and `link.active` take its blue l1.
The syntax colours are thememirror 2.0.1's, as the old app showed them (the Rosé Pine Dawn and Ayu Light columns of
the old values table), and the editor's active line and line numbers come from the palette (highlightLow and muted;
editor.line and editor.lineNumber.normal). Upstream's own contrast stays as it is: Ayu Light's syntax colours read at
about 2:1, and both themes' warning fill takes light text at about 2:1.

## Assembly Light

The design system's structure on white, with the same role names: `surface` #ffffff (the work panel), `rail` #f4f4f4,
`raised` #f7f7f7, `field` #f0f0f0, `chip` #ebebeb, `selected` #e6e6e6, `selected-strong` #dedede, `line` and
`line-soft` black at 0x1a and 0x0f; `ink` #0d0d0d down to `ink-faint` #666666; `primary` #0d0d0d with `on-primary`
#ffffff; the accents and states darkened to read on white (`accent` #4f57d6, `aqua` #0b7471, `lavender` #6d45c4,
`mint` #14774a, `blue` #1769a8, `amber` #865f00, `red` #bb3a32). Every ink and accent reads at 4.5:1 or better on
`surface`, `rail` and `raised`. On `selected` the faint ink and the accents are 4.4 to 4.6, and on `selected-strong`
about 4.1 to 4.3. The syntax colours follow the same roles. Jesse tunes it from here.

## Glass (assembly-glass.json)

The approved look is solid; Glass stays as an optional theme (Jesse asked for "somewhat glass", 11:48Z). It is
Assembly's values with the window background blurred (`WindowBackgroundAppearance::Blurred`) on macOS and the two
grounds translucent at 0x78 (0.47): `background` (`surface`, the work panel) and `sidebar.background`,
`title_bar.background` and `status_bar.background` (`rail`).

- Each dock split frame paints `tab_bar.background` (dock.rs:66) under its tab groups, and each tab group paints
  `background` (tab_panel.rs:713); tab strips paint `tab_bar.background` too (tab_panel.rs:495 and 688). The window
  root paints nothing of its own (gpui-base's Root doesn't; gpui-component's Root, if a window uses it, paints
  `background`, root.rs:723). `tab_bar.background` is clear in every theme, so a pane's ground is the one
  `background` fill.
- The rail keys show only once the frame paints the rail from `sidebar.background` (g0-shape's restyle).
- The rule for other lanes: a view whose text has to stay readable paints its own opaque ground. The chat column
  (g0-session) and code views paint `list.background` (surface) or highlight `editor.background`, which stay opaque
  here, and so does `popover.background`.
- The kit caps `list.active.background` at 20% and `selection.background` at 30% opacity (schema.rs:1026-1056), in
  every theme.

## Left for other lanes

- Terminal palettes (the old terminalPalettes.ts) come with the terminal lane, as the 2026-10-26 plan says, along with
  its behaviours: the terminal follows each theme and stays readable on it (old b3e90337, 95ef0d0d), and a theme
  change remakes it (34223b94).
- The person's own font and size choices come with preferences, after the cut.
- Not ported: the Monaco theme objects, contrast.ts (no tests on colour), the bundled fonts.

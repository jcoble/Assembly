# Themes

Each file here is one gpui-kit theme (gpui-component 0.8.1's `ThemeSet` with one theme in it). The setting is one
theme id, the file's stem; `src/core/theme/theme_id.rs` lists the ids, and an id that isn't one of them gives
`assembly`. There is no light/dark toggle: every theme is `mode: dark`, so picking one never switches mode.

The kit watches this folder and re-reads a changed file, then re-applies the theme in force. That works only while
no file says `is_default: true` (the kit skips a reloaded default theme, registry.rs:274-287), so every file says
false.

## Roster

| Id | File | Kit name | Comes from |
| --- | --- | --- | --- |
| assembly | assembly.json | Assembly | The design system Assembly (Jesse, 2026-10-23), via the GPUI spike's file |
| assembly-glass | assembly-glass.json | Assembly Glass | New: Assembly with a blurred window behind translucent grounds (macOS only) |
| houston | houston.json | Houston | mac-command-bar themeRegistry.ts HOUSTON |
| dracula | dracula.json | Dracula | themeRegistry.ts DRACULA |
| tokyo-night | tokyo-night.json | Tokyo Night | themeRegistry.ts TOKYO_NIGHT |
| graphite | graphite.json | Graphite | themeRegistry.ts GRAPHITE |
| amy, ayu-light, cobalt, rose-pine-dawn, tomorrow | `<id>.json` | Amy, Ayu Light, Cobalt, Rose Pine Dawn, Tomorrow | themeRegistry.ts THEMEMIRROR_THEMES: the Assembly shell with thememirror 2.0.1's syntax colours |

Off macOS, `assembly-glass` resolves to `assembly`, since Linux has no reliable blur.

## What every theme sets

- `font.size` 13 (body), `mono_font.size` 12 (the design system's `code`, 12/20), `radius` 6, `radius.lg` 8,
  `shadow` false. The fonts stay unset, which is the system face and system monospace.
- The kit has only two radii. The design system's 4 px (filters, fields) and 5 px (list and tree rows) are set by the
  components that draw those.
- Motion: theme files can't set it. The kit's `cx.theme().motion` (gpui-component theme/motion.rs) is the one place
  animation timing lives, and every lane's animations read it instead of writing their own durations (Jesse,
  2026-10-27: "small animations wherever we can"): `duration_fast` (120 ms) for hover fills and fades,
  `duration_normal` (180 ms) for menus, popovers, the drawer and panes moving, `easing_enter`, `easing_exit` and
  `easing_move` for their curves, and `spring_control` and `spring_move` for anything dragged or springing. The
  kit's slow step and move spring are 280 ms (motion.rs:28, 36; tab indicators use the spring, tab/tab_bar.rs:249),
  past the design system's 180, so `set` brings both down to 180 ms. Reduced motion is the kit's too: its components
  already settle at rest when the system asks for it.
- Kit 0.8.1 has no highlight keys for added, removed and changed lines (highlighter/registry.rs:318-349), so the
  design system's `add-bg`, `del-bg`, `mint` and `amber` for diffs belong to the diff view that draws them, not to
  these files.

## Kit key from design-system role

These are the keys in assembly.json, matched to the design system's tokens by value.

| Design-system role | Kit keys |
| --- | --- |
| surface | `background`, `accordion.background`, `list.background`, `list.even.background`, `list.head.background`, `table.background`, `tab_bar.background`, `title_bar.background`, highlight `editor.background` |
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

## The five ThemeMirror entries

Each is Assembly's file with its own `highlight.syntax` from thememirror 2.0.1. The kit has no theme inheritance, so
Assembly's `colors` are copied into these five files; that is the one place the colours repeat.

The editor background stays Assembly's `surface`. In the old app these entries also replaced the editor background
(Ayu Light's is #fcfcfc, a light editor in a dark shell), but the 2026-10-26 decision says they "change only the
editor's syntax colours", and the editor uses the theme's panel colours (old commit 1a06d74e). That follows the
decision; it's one key per file to change if Jesse wants the old light editors back.

## Glass (assembly-glass.json)

The window background is blurred (`WindowBackgroundAppearance::Blurred`) on macOS. What shows the blur:

- The kit's root paints `background` over the whole window (root.rs:723), and every dock tab group paints it again
  (tab_panel.rs:713); split frames and tab strips paint `tab_bar.background` (dock.rs:66, tab_panel.rs:495 and 688).
  So `background` and `tab_bar.background` are the translucent ones. The sessions column, chat, drawer and dock all
  share that one ground; `sidebar`, `status_bar` and `title_bar` do nothing for the dock.
- The layers stack (root, split frame, tab group), so the opacity on screen is higher than any one value. It's tuned
  on the Mac from screenshots.
- The rule for other lanes: a view whose text has to stay readable paints its own opaque ground. The chat column
  (g0-session) and code views paint `list.background` (surface) or highlight `editor.background`, which stay opaque here.
  `popover.background` stays opaque too.
- The kit caps `list.active.background` at 20% and `selection.background` at 30% opacity (schema.rs:1026-1056), in every theme.

## Left for other lanes

- Terminal palettes (the old terminalPalettes.ts) come with the terminal lane, as the 2026-10-26 plan says, along with
  its behaviours: the terminal follows each theme and stays readable on it (old b3e90337, 95ef0d0d), and a theme
  change remakes it (34223b94).
- The person's own font and size choices come with preferences, after the cut.
- Not ported: the Monaco theme objects, contrast.ts (no tests on colour), the bundled fonts.

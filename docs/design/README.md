Assembly is a workbench for running coding agents. It should feel like a calm, current IDE: one dark sheet split by soft lines, with tight spacing and a few muted neon colours that mean something. Build every screen from these rules and the tokens they name.

## Principles

- **One ground, flush panels.** The rail sits on `rail`, and everything else sits on `surface`: the tab row, the main view and every drawer tab. Panels touch, with no gaps, cards or outer borders.
- **Lines, not boxes.** Panels are divided by `line` hairlines that stop short of the edges (see Separator). Only the two vertical panel dividers run the full height.
- **Colour means something.** Neutrals carry the layout. Periwinkle `accent`, `aqua` and `lavender` mark focus and kinds of thing, and `mint`, `blue`, `amber` and `red` mark state. Nothing is coloured just for emphasis.
- **Quiet controls.** Use small squares and text tabs, not pills or badges. The only filled button is the off-white `primary`, and the only badge is the Board count.
- **Smaller, dimmer big text.** The largest text in the app is the 15px `title`. Headings use `ink`, not white.

## Colour

- **Grounds.** Use `surface` for panels and `rail` for the session rail. `field` holds inputs and a hovered row, `raised` the composer, and `chip` the filter buttons and the user's message bubble. `selected` marks an active tab or row, and `selected-strong` a selected tree row or segment.
- **Text.** It steps down through five tokens:
  - `ink`: headings and active items.
  - `ink-body`: reading text.
  - `ink-2`: secondary list text.
  - `ink-muted`: labels and toolbars.
  - `ink-faint`: meta lines, ages, placeholders and line numbers.

  Every one holds at least 4.5:1 on the grounds its usage note names. Inactive tabs use `ink-tab`, and tab-row icons `ink-icon`.
- **Accents.**
  - `accent` (periwinkle) is for the active underline, a running session's title, the caret and focus.
  - `aqua` marks SQL and CSS files.
  - `lavender` marks inline code, Markdown and merged pull requests.
- **States.** `mint` means good, done, added or open. `blue` means running, `amber` means it needs you, and `red` means failed or removed. Always pair a state colour with a sign, a word or a position, because `mint` and `red` sit close in lightness.
- **Diffs.** Use `add-bg`, `del-bg` and `hunk-bg` with `hunk-ink`.

## Type

- **Faces.** The interface uses the system face (`font-ui`), and code uses the system monospace (`font-mono`). No web fonts load.
- **Sizes.** The scale is `title` 15/20 semibold, `heading` 14, `body` 13 (the floor for a row's name), `small` 12, `meta` 11.5 and `label` 11 (semibold, capitals, 0.06em tracking). The Files tree uses `tree` at 15px so it scans quickly.
- **Code.** Use `code` (12/20) for diffs and code blocks, and `code-inline` for code inside prose.
- **Case.** Use sentence case everywhere except group labels.

## Space and density

The spacing steps are `space-2`, `space-4`, `space-6`, `space-8`, `space-10`, `space-12`, `space-14`, `space-16` and `space-28`.

- **Gutters.** The rail uses `space-14`, the main view `space-16` and the drawer `space-8` to `space-12`. The chat column has `space-28` side padding and is at most `chat-width` wide.
- **Rows.** A tab is `tab-height` (30px), the tab row and the Board row are `bar-height` (40px), and a tree row is `tree-row` (26px). A session row has 8px of vertical padding. Keep it tight: padding comes from these steps, never from larger text.

## Layout

From left to right, the window is:

1. The session rail, `rail-width` (300px).
2. A full-height `line` divider.
3. The main column: the tab row above the current view.
4. A full-height `line` divider.
5. The drawer, `drawer-width` (280px): the Board row above the drawer tabs.

There is no bottom status bar: Remote, Usage, CPU, memory and turns sit at the foot of the rail.

The drawer follows the tabs:
- Opening a Browser or Pull requests tab closes the drawer if it is showing Files.
- Opening an editor or Changes tab brings Files back.
- While Changes is open, Files narrows to the changed files with their `+n −n` counts.
- Other drawer tabs (Tasks, Terminal, Source control, Agents, Worktrees, Run, Context, History) stay open.
- Opening or closing the drawer by hand always wins.

## Shape

The radii are `radius-xs` (4px) for filters, fields, segments and the Board count, `radius-sm` (5px) for list and tree rows, `radius-md` (6px) for tabs and buttons, and `radius-lg` (8px) for the composer and bubbles. `radius-full` is kept for the Board button and a tab's close disc. Nothing has a shadow.

## Iconography

- **Style.** Use Lucide line icons (`@lucide/svelte`), as the app does today: 16px in the tab row and 14px in toolbars and the drawer, stroke 1.8, in the text colour of their context. The provider glyphs in session rows are text characters coloured `provider-orange` or `provider-blue`.
- **File icons.** These take the colour of their type.
- **No emoji** in the interface.

## Motion

Motion is short and finite: 120 to 180ms fades and slides for menus, the drawer and hover fills. Every animation ends, so nothing loops while idle. Respect reduced motion.

## Writing

Write plainly, in sentence case, with the user's own words for things: "Open another project", "Filter changed files", "Worked for 41s · ran 6 commands · edited 4 files". Join meta parts with " · ".

# The /next component kit

Everything a person clicks in the /next shell comes from this folder. The point
is not that the components exist — it is that a control looks and behaves the
same wherever it turns up. These are the rules that keep that true. Read them
before adding a component or styling a control by hand.

## Where the colors come from

No component in this folder names a color. Each one uses a shadcn slot
(`bg-card`, `text-muted-foreground`, `border-input`, `ring-ring`), and
`src/lib/shell/styles/next.css` points those slots at the shell's own tokens in
`nextTokens.css`. Change the palette there and every control follows.

| What you mean          | What to write                                         |
| ---------------------- | ----------------------------------------------------- |
| The window behind it all | `bg-background`                                     |
| A raised sheet: a card, a panel body | `bg-card`                             |
| A menu, dialog or tooltip | `bg-popover`                                       |
| A quiet filled control, a selected segment | `bg-secondary`                    |
| Hover fill, highlighted menu row | `bg-accent` (this is **not** the brand mint) |
| The mint accent — a send button, a slider's filled track | `bg-primary` |
| Body text / secondary text | `text-foreground` / `text-muted-foreground`      |
| A hairline | `border` with no color, or `ring-1 ring-border`                |

Two tokens the slots have no name for are read directly and only for text
tiers: `var(--color-text-2)` and `var(--color-text-3)`.

`nextTokens.css` is a closed list — a test fails the build if a name is added
to it that no component reads. Reach for an existing token before inventing one.

## Type sizes are not Tailwind's

Inside /next the app's own scale wins: **`text-sm` is 12px and `text-xs` is
11px**, not 14/12. The floor for anything a person reads as content — a menu
row, an option, a field's value, a setting's title — is **13px**, written
`text-[13px]`. Use `text-sm` (12px) for metadata beside it and `text-xs` (11px)
only for a group heading in small caps.

## Shape and spacing

- Radii come from the shell's three-step scale: 8px (`rounded-md`), 12px
  (`rounded-lg`, the default for controls), 16px (`rounded-xl`, for panels and
  the message box). Pills use `rounded-full`.
- Spacing comes from six steps, named in `nextTokens.css`: `--space-1` 4px,
  `--space-2` 8px, `--space-3` 12px, `--space-4` 16px, `--space-5` 24px,
  `--space-6` 32px. Inside a control, 8px; between rows, 12px; between cards,
  16px; around a whole section, 24 to 32px.
- Text sizes for anything a person reads come from three names:
  `--text-heading` (16px, weight `--text-heading-weight`) for the line that
  names a card or section, `--text-body` (14px) for body copy, `--text-quiet`
  (13px) for the metadata beside it. 11 and 12px are for small-caps group
  labels only.
- Depth is layering, not outlines: a raised surface plus `shadow-sm`. **A card
  carries no outline at all** — no ring, no border. It is told apart from the
  page by its own fill, the way it is on Spotify and in Material 3. Hairlines
  are for sheets that float over the page (menus, dialogs, tooltips) and for
  dividing rows inside a surface. Do not add a border to signal state.

## Every interactive control owes four things

1. **A hit target of at least 24px**, 28px preferred. `Button size="xs"` is 24,
   `sm` is 28, the default is 32. A small target may grow its hit area with an
   invisible `after:absolute after:-inset-2` rather than growing visually.
2. **A visible focus state.** A button, a row or a menu item takes the ring:
   `focus-visible:ring-ring/50 focus-visible:ring-3`. A field — anything a
   person types into — takes its own edge instead:
   `focus-visible:border-(color:--focus-border)`, which turns the border mint
   without drawing a halo around the control. Never `outline: none` without
   one of the two.
3. **A hover state** — `hover:bg-accent/60 hover:text-foreground` for quiet
   controls; a filled control brightens instead.
4. **A disabled state**: `disabled:opacity-50 disabled:pointer-events-none`,
   and the control must still say why it is off if that is not obvious.

## Which component for which job

| Job | Use |
| --- | --- |
| A command: Send, Done, Reset | `Button` (`default` for the one primary action on screen, `ghost` for everything else) |
| An icon on its own | `IconButton` — it makes the label mandatory and shows it as a tooltip |
| One choice out of 5 or more, or long labels | `Select` (bits-ui). **Never a native `<select>`** — it cannot be styled and comes out as the OS control |
| One choice out of 2–4 short labels worth seeing at once | `SegmentedControl` |
| On or off, taking effect immediately | `Switch` |
| A number on a range | `Slider`, with the value shown beside it |
| Actions, or a setting changed from a compact trigger | `DropdownMenu`, with `Sub` when a row has its own list of values |
| Actions for the row or region under the pointer | `ContextMenu`; attach its trigger only to that target so other native context menus remain available |
| A few icon actions that appear on a list row when it is hovered | `HoverActions` with `HoverActionButton` inside |
| Naming what an icon does | `Tooltip` (already inside `IconButton`) |
| Grouping rows of settings or details | `Card`, or a plain `rounded-xl border` box with rows divided by `border-b` |

## Row actions on hover

Every list row in the app reveals its actions the same way: `HoverActions`
holding two or three `HoverActionButton`s. They are bare icon buttons with
nothing behind them — no pill, no panel, no shadow — sitting over the row's own
metadata, and each one lights up by itself on hover rather than as a block.
They are always in the page and only fade, so revealing them cannot rebuild a
subtree or shift the row's text; the row supplies the trigger by carrying
`class="group"`. A button's `tone` names where the action leads (`primary` for
the session, `info` for a file or editor surface, `success` for source control)
rather than naming a color, and `info` and `success` are the one place a kit
component reads the shell's status tokens directly, because the registry has no
slot for either idea.

## Divergences from the shadcn registry, and why

Vendored components stay verbatim where they can, so the next copy from the
registry is a clean paste. These are the deliberate exceptions:

- `dropdown-menu-item`, `dropdown-menu-sub-trigger`, `context-menu-item`, `select-item`: 13px text
  and `min-h-6`, per the floor and hit-target rules above. Menu rows also style
  `data-highlighted` alongside `focus:`, because keyboard movement through a
  bits-ui menu marks the row rather than focusing it.
- `input`: 13px, and the registry's `text-base md:text-sm` pair is dropped —
  this app has one size.
- `slider`: the thumb took a literal white; it now uses `bg-foreground` so a
  theme change carries it, at 14px with a 28px grab area.
- `tooltip-content`: the registry inverts the tooltip (light on dark-ish). In a
  dark shell that reads as an alarm, so it sits on `bg-popover` with a hairline
  ring and the standard shadow.
- `segmented-control` and `icon-button` are ours; the registry has neither.

## One more trap worth knowing

The vendored components were written against a newer bits-ui than this app
pins. The newer one writes `data-checked`; the pinned one writes
`data-state="checked"`. `next.css` redefines five variants
(`data-checked`, `data-unchecked`, `data-active`, `data-open`, `data-closed`)
so both spellings work, and `scripts/nextTokens.test.mjs` fails on any other
bare `data-…:` class that matches nothing. If you vendor a new component, run
that test before trusting its states.

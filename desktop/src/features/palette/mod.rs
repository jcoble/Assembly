//! The Cmd-K command palette: the kit's `Command` over the commands features registered (core/commands), in a kit
//! dialog, so it opens and closes with the kit's dialog motion and native views step aside while it's up (core/
//! native_views). Replaces CommandPaletteOverlay.svelte and PalettePanel.svelte.

pub mod palette_view;

gpui_kit::actions!(palette, [TogglePalette]);

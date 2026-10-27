//! The Cmd-K command palette: the kit's `Command` over the commands features registered (core/commands). While it's
//! open, native views step aside (core/native_views). Replaces CommandPaletteOverlay.svelte and PalettePanel.svelte.

pub mod palette_view;

gpui_kit::actions!(palette, [TogglePalette]);

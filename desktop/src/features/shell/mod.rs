//! The window's shape: the sessions column, the center, the tools drawer and the bottom dock on the kit's DockArea,
//! with the top bar above and the utility strip below. Replaces mac-command-bar's ShellFrame, frame.ts and the frame
//! markup in routes/+page.svelte. Panes are empty placeholders until the lanes that own them register real ones.

pub mod default_layout;
pub mod frame;
pub mod home_button;
pub mod layout_store;
pub mod placeholder_pane;
pub mod top_bar;
pub mod utility_strip;

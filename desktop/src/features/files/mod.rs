//! The left column's Files and Search panes: the selected session's folder as a tree that loads a folder when it's
//! opened (`files/list`), and a search over its file and folder names (`files/search`). Replaces the old app's
//! FilesPanel.svelte tree and its Search tab, which searched names only.

#[path = "editor/document_store.rs"]
pub mod document_store;
#[path = "editor/editor_surface.rs"]
pub mod editor_surface;
pub mod entry;
#[path = "editor/file_history.rs"]
pub mod file_history;
#[path = "editor/file_save.rs"]
pub mod file_save;
#[path = "editor/file_text.rs"]
pub mod file_text;
pub mod file_tree;
#[path = "editor/file_view.rs"]
pub mod file_view;
pub mod files_pane;
pub mod search_pane;

#[cfg(test)]
mod files_real_tests;
#[cfg(test)]
mod files_reload_real_tests;
#[cfg(test)]
mod search_pane_tests;

#[path = "editor/file_comparison.rs"]
mod file_comparison;

mod file_actions;

mod file_action_form;

#[path = "editor/file_codec.rs"]
mod file_codec;

mod tree_codec;

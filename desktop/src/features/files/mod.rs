//! The left column's Files pane: the selected session's folder as a tree that loads a folder when it's opened
//! (`files/list`), with a filter over its file and folder names (`files/search`). Replaces the old app's
//! FilesPanel.svelte; its filter replaces the Search tab.

#[path = "editor/document_store.rs"]
pub mod document_store;
#[path = "editor/editor_surface.rs"]
pub mod editor_surface;
pub mod entry;
pub mod file_filter;
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
pub mod panel_message;

#[cfg(test)]
mod files_entry_real_tests;
#[cfg(test)]
mod files_filter_real_tests;
#[cfg(test)]
mod files_header_real_tests;
#[cfg(test)]
mod files_message_real_tests;
#[cfg(test)]
mod files_real_tests;
#[cfg(test)]
mod files_reload_real_tests;

#[path = "editor/file_comparison.rs"]
mod file_comparison;

mod file_actions;
pub mod file_entry_bar;

#[path = "editor/file_codec.rs"]
mod file_codec;

mod tree_codec;

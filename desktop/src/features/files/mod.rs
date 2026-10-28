//! The left column's Files and Search panes: the selected session's folder as a tree that loads a folder when it's
//! opened (`files/list`), and a search over its file and folder names (`files/search`). Replaces the old app's
//! FilesPanel.svelte tree and its Search tab, which searched names only.

pub mod entry;
pub mod file_text;
pub mod file_tree;
pub mod file_view;
pub mod files_pane;
pub mod search_pane;

#[cfg(test)]
mod files_real_tests;
#[cfg(test)]
mod search_pane_tests;

//! `FileTree` owns what the Files pane knows of a session's folder: each loaded folder's entries, which folders are
//! open, which are loading and why one failed. It draws nothing; the pane loads what it asks for and draws `rows`.

use std::collections::{HashMap, HashSet};

use super::entry::{FileEntry, FileListing};

/// One line of the tree, in drawing order.
#[derive(Debug)]
pub enum Row<'a> {
    Entry {
        entry: &'a FileEntry,
        depth: usize,
        open: bool,
    },
    /// The folder above is loading.
    Loading { depth: usize },
    Failed { depth: usize, message: &'a str },
    /// The folder above held more than the backend answers at once.
    Truncated { depth: usize },
}

/// The root folder is "", as `files/list` takes it.
#[derive(Default)]
pub struct FileTree {
    loaded: HashMap<String, FileListing>,
    open: HashSet<String>,
    loading: HashSet<String>,
    failed: HashMap<String, String>,
}

impl FileTree {
    /// Starts loading the root and returns it, for the pane to load.
    pub fn start(&mut self) -> &'static str {
        todo!()
    }

    /// Opens or closes a folder. Opening one that isn't loaded starts loading it and returns it; closing one drops
    /// its load, so the pane drops its call.
    pub fn toggle(&mut self, dir: &str) -> Option<String> {
        todo!()
    }

    /// Whether `dir` is still loading, so the pane keeps its call only while it is.
    pub fn is_loading(&self, dir: &str) -> bool {
        todo!()
    }

    /// Takes a folder's answer; one for a folder no longer loading is dropped.
    pub fn loaded(&mut self, dir: &str, answer: Result<FileListing, String>) {
        todo!()
    }

    /// The lines to draw: the root's entries, each open folder's under it.
    pub fn rows(&self) -> Vec<Row<'_>> {
        todo!()
    }
}

#[cfg(test)]
#[path = "file_tree_tests.rs"]
mod tests;

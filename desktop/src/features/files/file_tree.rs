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
    Loading {
        depth: usize,
    },
    Failed {
        depth: usize,
        message: &'a str,
    },
    /// The folder above held more than the backend answers at once.
    Truncated {
        depth: usize,
    },
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
        *self = Self::default();
        self.loading.insert(String::new());
        ""
    }

    /// Opens or closes a folder. Opening one that isn't loaded starts loading it and returns it; closing one drops
    /// its load, so the pane drops its call.
    pub fn toggle(&mut self, dir: &str) -> Option<String> {
        if self.open.remove(dir) {
            self.loading.remove(dir);
            return None;
        }
        self.open.insert(dir.to_string());
        if self.loaded.contains_key(dir) {
            return None;
        }
        self.failed.remove(dir);
        self.loading.insert(dir.to_string());
        Some(dir.to_string())
    }

    /// Takes a folder's answer; one for a folder no longer loading is dropped.
    pub fn loaded(&mut self, dir: &str, answer: Result<FileListing, String>) {
        if !self.loading.remove(dir) {
            return;
        }
        match answer {
            Ok(listing) => {
                self.loaded.insert(dir.to_string(), listing);
            }
            Err(message) => {
                self.failed.insert(dir.to_string(), message);
            }
        }
    }

    /// The lines to draw: the root's entries, each open folder's under it.
    pub fn rows(&self) -> Vec<Row<'_>> {
        let mut rows = Vec::new();
        self.push(&mut rows, "", 0);
        rows
    }

    fn push<'a>(&'a self, rows: &mut Vec<Row<'a>>, dir: &str, depth: usize) {
        if self.loading.contains(dir) {
            rows.push(Row::Loading { depth });
        } else if let Some(message) = self.failed.get(dir) {
            rows.push(Row::Failed { depth, message });
        } else if let Some(listing) = self.loaded.get(dir) {
            for entry in &listing.entries {
                let open = entry.is_directory && self.open.contains(&entry.path);
                rows.push(Row::Entry { entry, depth, open });
                if open {
                    self.push(rows, &entry.path, depth + 1);
                }
            }
            if listing.truncated {
                rows.push(Row::Truncated { depth });
            }
        }
    }
}

#[cfg(test)]
#[path = "file_tree_tests.rs"]
mod tests;

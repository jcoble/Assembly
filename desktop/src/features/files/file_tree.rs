//! `FileTree` owns what the Files pane knows of a session's folder: each loaded folder's entries, which folders are
//! open, which are loading and why one failed. It draws nothing; the pane loads what it asks for and draws `visible`.
//! The list of lines is rebuilt when the tree changes, so a draw reads only the lines it shows.

use gpui_kit::SharedString;
use std::collections::{HashMap, HashSet};
use std::ops::Range;

use super::entry::{FileEntry, FileListing, PageCursor};

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
        dir: &'a str,
    },
}

pub enum RevealStep {
    Load {
        dir: String,
        cursor: Option<PageCursor>,
    },
    Waiting,
    Ready,
    Missing,
}

/// Where a row's text lives, so the list costs little however long the folders' entries are.
enum Line {
    Entry {
        dir: SharedString,
        at: usize,
        depth: usize,
        open: bool,
    },
    Loading {
        depth: usize,
    },
    Failed {
        dir: SharedString,
        depth: usize,
    },
    Truncated {
        dir: SharedString,
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
    lines: Vec<Line>,
}

impl FileTree {
    /// Starts loading the root and returns it, for the pane to load.
    pub fn start(&mut self) -> &'static str {
        *self = Self::default();
        self.loading.insert(String::new());
        self.rebuild();
        ""
    }

    /// Opens or closes a folder. Opening one that isn't loaded starts loading it and returns it; closing one drops
    /// its load, so the pane drops its call.
    pub fn toggle(&mut self, dir: &str) -> Option<String> {
        if self.open.remove(dir) {
            let prefix = dir.to_owned() + "/";
            self.loading
                .retain(|path| path != dir && !path.starts_with(&prefix));
            self.open.retain(|path| !path.starts_with(&prefix));
            self.loaded
                .retain(|path, _| path != dir && !path.starts_with(&prefix));
            self.failed
                .retain(|path, _| path != dir && !path.starts_with(&prefix));
            self.rebuild();
            return None;
        }
        self.open.insert(dir.to_string());
        if self.loaded.contains_key(dir) {
            self.rebuild();
            return None;
        }
        self.failed.remove(dir);
        self.loading.insert(dir.to_string());
        self.rebuild();
        Some(dir.to_string())
    }

    pub fn more(&mut self, dir: &str) -> Option<PageCursor> {
        if self.loading.contains(dir) {
            return None;
        }
        let cursor = self.loaded.get(dir)?.next_cursor.clone()?;
        self.failed.remove(dir);
        self.loading.insert(dir.to_owned());
        self.rebuild();
        Some(cursor)
    }

    /// Takes a folder's answer; one for a folder no longer loading is dropped.
    pub fn loaded(&mut self, dir: &str, answer: Result<FileListing, String>) {
        if !self.loading.remove(dir) {
            return;
        }
        match answer {
            Ok(listing) => {
                if let Some(previous) = self.loaded.get_mut(dir) {
                    let mut paths: HashSet<_> =
                        previous.entries.iter().map(|e| e.path.clone()).collect();
                    previous.entries.extend(
                        listing
                            .entries
                            .into_iter()
                            .filter(|e| paths.insert(e.path.clone())),
                    );
                    previous.truncated = listing.truncated;
                    previous.next_cursor = listing.next_cursor;
                } else {
                    self.loaded.insert(dir.to_string(), listing);
                }
            }
            Err(message) => {
                self.failed.insert(dir.to_string(), message);
            }
        }
        self.rebuild();
    }

    /// Nothing is loading any more; what's loaded stays.
    pub fn cancel_loads(&mut self) {
        self.loading.clear();
        self.rebuild();
    }

    pub fn entry_state(&self, path: &str) -> Option<(bool, bool)> {
        let rows = self.visible(0..self.row_count());
        rows.into_iter().find_map(|row| match row {
            Row::Entry { entry, open, .. } if entry.path == path => {
                Some((entry.is_directory, open))
            }
            _ => None,
        })
    }

    pub fn adjacent(&self, selected: Option<&str>, delta: i32) -> Option<(String, bool, usize)> {
        let entries: Vec<_> = self
            .visible(0..self.row_count())
            .into_iter()
            .enumerate()
            .filter_map(|(ix, row)| match row {
                Row::Entry { entry, .. } => Some((entry.path.to_string(), entry.is_directory, ix)),
                _ => None,
            })
            .collect();
        if entries.is_empty() {
            return None;
        }
        let at =
            selected.and_then(|selected| entries.iter().position(|(path, _, _)| path == selected));
        let index = at
            .map(|at| (at as i64 + i64::from(delta)).clamp(0, entries.len() as i64 - 1) as usize)
            .unwrap_or(0);
        Some(entries[index].clone())
    }

    pub fn reveal_failed(&self, path: &str) -> bool {
        self.failed.keys().any(|failed| {
            failed.is_empty() || path == failed || path.starts_with(&(failed.clone() + "/"))
        })
    }

    pub fn expanded_paths(&self) -> Vec<String> {
        let mut paths: Vec<_> = self.open.iter().cloned().collect();
        paths.sort();
        paths
    }

    /// Loads only the pages needed to reveal a folder, keeping its ancestors expanded.
    pub fn reveal(&mut self, path: &str) -> RevealStep {
        self.reveal_path(path, true)
    }
    pub fn reveal_entry(&mut self, path: &str) -> RevealStep {
        self.reveal_path(path, false)
    }
    fn reveal_path(&mut self, path: &str, expand_last: bool) -> RevealStep {
        let step = self.walk_to(path, expand_last);
        self.rebuild();
        step
    }
    fn walk_to(&mut self, path: &str, expand_last: bool) -> RevealStep {
        let mut parent = String::new();
        let mut parts = path.split('/').filter(|part| !part.is_empty()).peekable();
        while let Some(part) = parts.next() {
            if self.loading.contains(&parent) {
                return RevealStep::Waiting;
            }
            let Some(listing) = self.loaded.get(&parent) else {
                return RevealStep::Missing;
            };
            let target = if parent.is_empty() {
                part.to_string()
            } else {
                format!("{parent}/{part}")
            };
            let last = parts.peek().is_none();
            if !listing
                .entries
                .iter()
                .any(|entry| entry.path == target && (entry.is_directory || (last && !expand_last)))
            {
                return match self.more(&parent) {
                    Some(cursor) => RevealStep::Load {
                        dir: parent,
                        cursor: Some(cursor),
                    },
                    None => RevealStep::Missing,
                };
            }
            if last && !expand_last {
                return RevealStep::Ready;
            }
            self.open.insert(target.clone());
            if self.failed.contains_key(&target) {
                return RevealStep::Missing;
            }
            if !self.loaded.contains_key(&target) {
                if !self.loading.insert(target.clone()) {
                    return RevealStep::Waiting;
                }
                return RevealStep::Load {
                    dir: target,
                    cursor: None,
                };
            }
            parent = target;
        }
        RevealStep::Ready
    }

    /// Whether `dir`'s entries are on screen: loaded, and it and every folder above it open.
    pub fn shows(&self, dir: &str) -> bool {
        if !self.loaded.contains_key(dir) {
            return false;
        }
        let mut at = dir;
        while !at.is_empty() {
            if !self.open.contains(at) {
                return false;
            }
            at = at.rsplit_once('/').map_or("", |(parent, _)| parent);
        }
        true
    }

    pub fn root_loaded(&self) -> bool {
        self.loaded.contains_key("")
    }

    /// Whether the root's load failed, so showing the tree again retries it (its folder may exist by now).
    pub fn root_failed(&self) -> bool {
        self.failed.contains_key("")
    }

    pub fn row_count(&self) -> usize {
        self.lines.len()
    }

    /// The lines in `range`, in drawing order: the root's entries, each open folder's under it.
    pub fn visible(&self, range: Range<usize>) -> Vec<Row<'_>> {
        let end = range.end.min(self.lines.len());
        let start = range.start.min(end);
        self.lines[start..end]
            .iter()
            .filter_map(|line| self.row(line))
            .collect()
    }

    fn row<'a>(&'a self, line: &'a Line) -> Option<Row<'a>> {
        Some(match line {
            Line::Entry {
                dir,
                at,
                depth,
                open,
            } => Row::Entry {
                entry: self.loaded.get(&**dir)?.entries.get(*at)?,
                depth: *depth,
                open: *open,
            },
            Line::Loading { depth } => Row::Loading { depth: *depth },
            Line::Failed { dir, depth } => Row::Failed {
                depth: *depth,
                message: self.failed.get(&**dir)?,
            },
            Line::Truncated { dir, depth } => Row::Truncated { depth: *depth, dir },
        })
    }

    fn rebuild(&mut self) {
        let mut lines = Vec::with_capacity(self.lines.len());
        self.push(&mut lines, "", 0);
        self.lines = lines;
    }

    fn push(&self, lines: &mut Vec<Line>, dir: &str, depth: usize) {
        let shared = SharedString::from(dir);
        if let Some(listing) = self.loaded.get(dir) {
            for (at, entry) in listing.entries.iter().enumerate() {
                let open = entry.is_directory && self.open.contains(entry.path.as_str());
                lines.push(Line::Entry {
                    dir: shared.clone(),
                    at,
                    depth,
                    open,
                });
                if open {
                    self.push(lines, &entry.path, depth + 1);
                }
            }
            if self.loading.contains(dir) {
                lines.push(Line::Loading { depth });
            } else {
                if self.failed.contains_key(dir) {
                    lines.push(Line::Failed {
                        dir: shared.clone(),
                        depth,
                    });
                }
                if listing.truncated {
                    lines.push(Line::Truncated { dir: shared, depth });
                }
            }
        } else if self.loading.contains(dir) {
            lines.push(Line::Loading { depth });
        } else if self.failed.contains_key(dir) {
            lines.push(Line::Failed { dir: shared, depth });
        }
    }
}

#[cfg(test)]
#[path = "file_tree_tests.rs"]
mod tests;

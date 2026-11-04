//! Which files the Changes tab is reading and what came back, as plain data. A status answer starts a round; only that
//! round's replies count. At most four files are read at once, the picked one first. Replaces the readAll half of
//! GitDiffView.svelte and splitDiffByFile.
use super::{
    branch_diff::BranchDiff,
    file_diff::{CUT, FileDiff},
};
use crate::core::socket::{checkout_selector::CheckoutSelector, row_source::ExpectedRowSource};
use crate::core::ui::changed_tree::tree_order;
use crate::core::ui::diff::{SuppliedPatch, supplied_patch};
use crate::features::source_control::git_status::GitStatus;
use gpui_kit::component::diff::DiffFile;
use serde_json::{Value, json};
use std::collections::hash_map::DefaultHasher;
use std::collections::{BTreeMap, BTreeSet, HashSet, VecDeque};
use std::hash::{Hash, Hasher};

/// Each read is a few short git processes.
pub const READS_AT_ONCE: usize = 4;
/// While a selection is first being read, the stack is redrawn this often rather than per file.
const SHOW_EVERY: usize = 8;

#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub enum Scope {
    /// The working copy against HEAD.
    #[default]
    Uncommitted,
    /// Everything since the branch left the default branch, committed or not.
    Branch,
}
impl Scope {
    pub fn saved(self) -> &'static str {
        match self {
            Scope::Uncommitted => "uncommitted",
            Scope::Branch => "branch",
        }
    }
    pub fn from_saved(saved: &str) -> Option<Self> {
        match saved {
            "uncommitted" => Some(Scope::Uncommitted),
            "branch" => Some(Scope::Branch),
            _ => None,
        }
    }
}
/// What every read of a round is pinned to: the status answer's session, source and checkout.
#[derive(Clone, Debug, PartialEq)]
pub struct Selection {
    pub session: String,
    pub source: Option<ExpectedRowSource>,
    pub checkout: Option<CheckoutSelector>,
}
impl Selection {
    pub fn args(&self, extra: Value) -> Value {
        let mut args = json!({"session":self.session,"expectedSource":self.source});
        if let (Some(args), Some(extra)) = (args.as_object_mut(), extra.as_object()) {
            args.extend(extra.clone());
        }
        CheckoutSelector::applied(self.checkout.as_ref(), args)
    }
}
#[derive(Clone, Debug, PartialEq)]
pub struct Wanted {
    pub path: String,
    pub original_path: Option<String>,
}
/// Uncommitted reads every changed file. Branch's one read brings every tracked file, leaving only the untracked.
pub fn wanted_files(status: &GitStatus, scope: Scope) -> Vec<Wanted> {
    status
        .files
        .iter()
        .filter(|file| scope == Scope::Uncommitted || file.untracked())
        .map(|file| Wanted {
            path: file.path.clone(),
            original_path: file.original_path.clone(),
        })
        .collect()
}
/// Marks a file's working-tree part. It sits in the patch's header, so a line of the file can't forge it.
pub const WORKING_PART: &str = "assembly-part: working tree";
pub fn is_working_part(file: &DiffFile) -> bool {
    file.extended_headers()
        .iter()
        .any(|line| &**line == WORKING_PART)
}
fn mark_working(patch: &str) -> String {
    let mut marked = String::with_capacity(patch.len() + WORKING_PART.len() + 1);
    for line in patch.split_inclusive('\n') {
        marked.push_str(line);
        if line.starts_with("diff --git ") {
            marked.push_str(WORKING_PART);
            marked.push('\n');
        }
    }
    marked
}
/// A patch the parser refuses, kept as Git's exact bytes.
#[derive(Clone, Debug, PartialEq)]
pub struct CombinedPatch {
    pub label: &'static str,
    pub text: String,
}
#[derive(Clone)]
pub struct ReadFile {
    pub digest: u64,
    pub files: Vec<DiffFile>,
    pub combined: Vec<CombinedPatch>,
    pub notes: Vec<&'static str>,
    /// Shows both a staged part and a working-tree part.
    pub both: bool,
}
fn digest(parts: &[&str], flags: (bool, bool)) -> u64 {
    let mut hasher = DefaultHasher::new();
    parts.hash(&mut hasher);
    flags.hash(&mut hasher);
    hasher.finish()
}
/// Staged lines above working-tree lines, as two entries under one path: the parser refuses hunks that overlap or run
/// out of order within one file, and the two sides can do both.
pub fn read_file(diff: &FileDiff) -> Result<ReadFile, String> {
    let mut file = ReadFile {
        digest: digest(
            &[&diff.staged, &diff.unstaged],
            (diff.binary, diff.truncated),
        ),
        files: Vec::new(),
        combined: Vec::new(),
        notes: diff.notes(),
        both: false,
    };
    let mut parts = diff.parts();
    let both = parts.len() == 2 && parts.iter().all(|(_, patch)| patch.contains("\n@@ "));
    for (label, patch) in parts.drain(..) {
        let marked = (both && label == "Working tree").then(|| mark_working(patch));
        match supplied_patch(marked.as_deref().unwrap_or(patch), diff.truncated)? {
            SuppliedPatch::Unified(files) => file.files.extend(files),
            SuppliedPatch::Combined(_) => file.combined.push(CombinedPatch {
                label,
                text: patch.to_string(),
            }),
        }
    }
    file.both = both && file.combined.is_empty();
    Ok(file)
}
/// The patch cut at each file's own header.
fn split_patch(patch: &str) -> Vec<&str> {
    let mut starts: Vec<usize> = Vec::new();
    let mut at = 0;
    for line in patch.split_inclusive('\n') {
        if line.starts_with("diff --") {
            starts.push(at);
        }
        at += line.len();
    }
    starts
        .iter()
        .enumerate()
        .map(|(ix, start)| &patch[*start..starts.get(ix + 1).copied().unwrap_or(patch.len())])
        .collect()
}
/// The path a file's header names, for a file the parser couldn't read.
fn header_path(chunk: &str) -> String {
    let header = chunk.lines().next().unwrap_or_default();
    match header.rfind(" b/") {
        Some(at) => header[at + 3..].to_string(),
        None => header.rsplit(' ').next().unwrap_or_default().to_string(),
    }
}
#[derive(Default)]
pub struct BranchFiles {
    pub files: Vec<(String, ReadFile)>,
    pub unreadable: Vec<String>,
}
/// One read per file, so a bad patch costs that file alone. Only the last file can be cut.
pub fn read_branch(branch: &BranchDiff) -> BranchFiles {
    let chunks = split_patch(&branch.diff);
    let mut read = BranchFiles::default();
    for (ix, chunk) in chunks.iter().enumerate() {
        let cut = branch.truncated && ix + 1 == chunks.len();
        let mut file = ReadFile {
            digest: digest(&[chunk], (false, cut)),
            files: Vec::new(),
            combined: Vec::new(),
            notes: if cut { vec![CUT] } else { Vec::new() },
            both: false,
        };
        match supplied_patch(chunk, cut) {
            Ok(SuppliedPatch::Unified(files)) => {
                let path = files
                    .first()
                    .map(|first| first.path().to_string())
                    .unwrap_or_else(|| header_path(chunk));
                file.files = files;
                read.files.push((path, file));
            }
            Ok(SuppliedPatch::Combined(text)) => {
                file.combined.push(CombinedPatch {
                    label: "Branch",
                    text,
                });
                read.files.push((header_path(chunk), file));
            }
            Err(_) => read.unreadable.push(header_path(chunk)),
        }
    }
    read
}

#[derive(Default)]
pub struct WorkingCopy {
    round: u64,
    queue: VecDeque<Wanted>,
    running: HashSet<String>,
    branch: bool,
    /// The paths this round reads one by one.
    expected: HashSet<String>,
    answers: BTreeMap<String, ReadFile>,
    unreadable: BTreeSet<String>,
    /// Nothing was on screen for this selection when the round began.
    fresh: bool,
    since_shown: usize,
    shown: Vec<(String, u64)>,
}
impl WorkingCopy {
    pub fn answers(&self) -> &BTreeMap<String, ReadFile> {
        &self.answers
    }
    pub fn unreadable(&self) -> impl Iterator<Item = &String> {
        self.unreadable.iter()
    }
    /// Stops the round. What it read stays on screen.
    pub fn stop(&mut self) {
        self.round += 1;
        self.queue.clear();
        self.running.clear();
        self.branch = false;
    }
    /// A different selection or scope: nothing here is about it.
    pub fn clear(&mut self) {
        self.stop();
        self.expected.clear();
        self.answers.clear();
        self.unreadable.clear();
        self.shown.clear();
        self.since_shown = 0;
    }
    /// A file no longer changed drops now. In Branch what's shown stays until its one answer replaces it.
    pub fn begin(&mut self, wanted: Vec<Wanted>, branch: bool, focus: Option<&str>) -> u64 {
        self.round += 1;
        self.fresh = self.answers.is_empty();
        self.since_shown = 0;
        self.unreadable.clear();
        self.expected = wanted.iter().map(|file| file.path.clone()).collect();
        if !branch {
            let expected = &self.expected;
            self.answers.retain(|path, _| expected.contains(path));
        }
        self.queue = wanted.into();
        self.running.clear();
        self.branch = branch;
        if let Some(focus) = focus {
            self.focus_first(focus);
        }
        self.round
    }
    pub fn round(&self) -> u64 {
        self.round
    }
    /// The picked file goes next, if it hasn't started.
    pub fn focus_first(&mut self, path: &str) {
        if let Some(at) = self.queue.iter().position(|file| file.path == path)
            && let Some(file) = self.queue.remove(at)
        {
            self.queue.push_front(file);
        }
    }
    /// The next file to start, while fewer than four are out.
    pub fn next_read(&mut self) -> Option<Wanted> {
        if self.running.len() >= READS_AT_ONCE {
            return None;
        }
        let next = self.queue.pop_front()?;
        self.running.insert(next.path.clone());
        Some(next)
    }
    /// What's still out or waiting. The branch read counts as one.
    pub fn pending(&self) -> usize {
        self.queue.len() + self.running.len() + usize::from(self.branch)
    }
    /// Whether this was the current round's. An unreadable file keeps nothing from before.
    pub fn answered(&mut self, round: u64, path: &str, file: Option<ReadFile>) -> bool {
        if round != self.round || !self.running.remove(path) {
            return false;
        }
        match file {
            Some(file) => {
                self.answers.insert(path.to_string(), file);
            }
            None => {
                self.answers.remove(path);
                self.unreadable.insert(path.to_string());
            }
        }
        self.since_shown += 1;
        true
    }
    /// `None` when the read failed. Either way this round's branch files replace the last round's.
    pub fn branch_answered(&mut self, round: u64, read: Option<BranchFiles>) -> bool {
        if round != self.round || !self.branch {
            return false;
        }
        self.branch = false;
        let expected = &self.expected;
        self.answers.retain(|path, _| expected.contains(path));
        if let Some(read) = read {
            self.since_shown += read.files.len();
            for (path, file) in read.files {
                if !self.expected.contains(&path) {
                    self.answers.insert(path, file);
                }
            }
            self.unreadable.extend(read.unreadable);
        }
        true
    }
    /// A commit's file, shown on its own.
    pub fn set_only(&mut self, path: String, file: ReadFile) {
        self.clear();
        self.answers.insert(path, file);
    }
    /// Whether the stack should be redrawn now. Redrawing resets its scroll, so a refresh waits for the round to end.
    pub fn should_show(&self) -> bool {
        self.pending() == 0
            || (self.fresh
                && !self.answers.is_empty()
                && (self.shown.is_empty() || self.since_shown >= SHOW_EVERY))
    }
    /// Whether what would be shown differs from what is, in tree order and by content. It remembers the answer.
    pub fn shown_changed(&mut self) -> bool {
        self.since_shown = 0;
        let now: Vec<(String, u64)> = tree_order(self.answers.keys().map(String::as_str))
            .into_iter()
            .map(|path| {
                let digest = self.answers[&path].digest;
                (path, digest)
            })
            .collect();
        if now == self.shown {
            return false;
        }
        self.shown = now;
        true
    }
}

#[cfg(test)]
#[path = "working_copy_tests.rs"]
mod tests;

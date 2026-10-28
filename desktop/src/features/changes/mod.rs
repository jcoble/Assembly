//! The Changes tab beside Chat: a changed file's diff from `git/diff` (docs/behavior/scm.md, scm#13 to scm#17).
//! Replaces the old app's GitDiffView.svelte and MultiFileDiff.svelte, one file at a time for now.

pub mod changes_pane;
pub mod diff_lines;
pub mod file_diff;

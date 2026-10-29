//! The Changes tab beside Chat: a changed file's diff from `git/diff`, or the branch's diff against its base from
//! `git/branchDiff` (docs/behavior/scm.md, scm#13 to scm#20). Replaces the old app's GitDiffView.svelte and
//! MultiFileDiff.svelte.

pub mod branch_diff;
pub mod changes_pane;
pub mod diff_lines;
pub mod file_diff;

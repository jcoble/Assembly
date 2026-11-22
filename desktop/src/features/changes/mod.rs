//! The Changes tab beside Chat: a changed file's diff from `git/diff`, or the branch's diff against its base from
//! `git/branchDiff` (Source control and worktrees: behavior list, https://app.notion.com/p/409394b0689d810ca866f3c09de23b25, scm#13 to scm#20). Replaces the old app's GitDiffView.svelte and
//! MultiFileDiff.svelte.

pub mod branch_diff;
pub mod changes_pane;
pub mod file_diff;
pub mod working_copy;

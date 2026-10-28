//! The drawer's Worktrees pane: the selected session's project's worktrees, from `worktrees/list`, read only while the
//! tab is shown (docs/behavior/scm.md). Replaces the old app's panels/worktrees/ and worktrees/worktreeManager*.ts.

pub mod worktree;
pub mod worktree_list;
pub mod worktree_row;
pub mod worktrees_pane;

//! The drawer's Source control pane: the selected session's project's branch and changed files, from `git/status`,
//! read only while the tab is shown (docs/behavior/scm.md). Replaces the old app's git/components/sourceControl/
//! status lists.

pub mod git_status;
pub mod source_control_pane;

pub mod commit_timeline;
pub mod git_action;
pub mod git_history;
pub mod git_operation;
pub mod graph_lanes;
pub mod history_filter;
pub mod history_pane;
#[cfg(test)]
mod pane_state_tests;
pub mod pull_requests;
pub mod remote_dialogs;
pub mod stash_dialogs;
pub mod sync_controls;

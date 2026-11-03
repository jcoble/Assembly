//! The drawer's Source control pane: the selected session's project's branch and changed files, from `git/status`,
//! read only while the tab is shown (docs/behavior/scm.md). Replaces the old app's git/components/sourceControl/
//! status lists. `git/` holds what the backend answers and the one write authority, `panel/` the pane and its menus and
//! dialogs, `history/` the commit list and the history tab.

#[path = "git/git_status.rs"]
pub mod git_status;
#[path = "panel/source_control_pane.rs"]
pub mod source_control_pane;

#[path = "panel/actions_menu.rs"]
pub mod actions_menu;
#[path = "panel/branch_menu.rs"]
pub mod branch_menu;
#[path = "panel/commit_box.rs"]
pub mod commit_box;
#[path = "history/commit_timeline.rs"]
pub mod commit_timeline;
#[path = "panel/discard_dialog.rs"]
pub mod discard_dialog;
#[path = "git/git_action.rs"]
pub mod git_action;
#[path = "git/git_branches.rs"]
pub mod git_branches;
#[path = "git/git_failure.rs"]
pub mod git_failure;
#[path = "history/git_history.rs"]
pub mod git_history;
#[path = "git/git_labels.rs"]
pub mod git_labels;
#[path = "git/git_operation.rs"]
pub mod git_operation;
#[path = "git/git_sync.rs"]
pub mod git_sync;
#[path = "git/git_target.rs"]
pub mod git_target;
#[path = "history/graph_lanes.rs"]
pub mod graph_lanes;
#[path = "history/history_filter.rs"]
pub mod history_filter;
#[path = "history/history_pane.rs"]
pub mod history_pane;
#[cfg(test)]
mod pane_state_tests;
pub mod pull_requests;
#[path = "panel/remote_dialogs.rs"]
pub mod remote_dialogs;
#[path = "panel/result_line.rs"]
pub mod result_line;
#[path = "panel/section_heading.rs"]
pub mod section_heading;
#[path = "panel/stash_dialogs.rs"]
pub mod stash_dialogs;

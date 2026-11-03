//! The header's more-actions menu as data, in the order it's drawn. A row that can't run says why on a second line,
//! because a disabled row never shows a tooltip. Publish is listed only while the branch has no upstream: one remote
//! publishes straight to it, several open a submenu so the remote is always chosen.
use super::git_status::GitStatus;

pub const STILL_RUNNING: &str = "Wait for the current source-control action to finish.";
pub const UNCONFIRMED: &str = "Refresh the last action's outcome before starting another.";
const NO_REPOSITORY: &str = "This panel is not pointed at a repository folder yet.";
const NO_UPSTREAM: &str =
    "This branch has no upstream branch, so there is no matching remote branch to pull from or push to.";
const READING_REMOTES: &str = "Reading this repository’s remotes…";

#[derive(Clone, Copy, Debug, PartialEq)]
pub enum MenuAction {
    Refresh,
    DiscardAll,
    Fetch,
    Pull,
    Push,
    AddRemote,
    BranchDiff,
    OpenHistory,
}
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum WriteState {
    Idle,
    Running,
    Unconfirmed,
}
pub struct MenuContext<'a> {
    pub has_session: bool,
    pub reading: bool,
    pub status: Option<&'a GitStatus>,
    /// `None` until `git/branches` has answered.
    pub remotes: Option<&'a [String]>,
    pub write: WriteState,
}
#[derive(Clone, Debug, PartialEq)]
pub struct MenuRow {
    pub action: MenuAction,
    pub label: &'static str,
    pub reason: Option<String>,
    pub separated: bool,
}
#[derive(Clone, Debug, PartialEq)]
pub enum PublishRow {
    Hidden,
    Blocked(String),
    One(String),
    Many(Vec<String>),
}
fn blocked(context: &MenuContext) -> Option<String> {
    if !context.has_session {
        return Some(NO_REPOSITORY.into());
    }
    match context.write {
        WriteState::Idle => None,
        WriteState::Running => Some(STILL_RUNNING.into()),
        WriteState::Unconfirmed => Some(UNCONFIRMED.into()),
    }
}
fn has_upstream(context: &MenuContext) -> bool {
    context.status.is_some_and(|status| status.has_upstream)
}
fn fetch_reason(context: &MenuContext) -> Option<String> {
    if has_upstream(context) {
        return None;
    }
    match context.remotes {
        None => Some(READING_REMOTES.into()),
        Some([]) => Some("This repository has no remote. Add one to fetch.".into()),
        Some(_) => None,
    }
}
pub fn rows(context: &MenuContext) -> Vec<MenuRow> {
    let write = |reason: Option<String>| blocked(context).or(reason);
    let upstream = |_: ()| (!has_upstream(context)).then(|| NO_UPSTREAM.to_string());
    let no_changes = context
        .status
        .is_none_or(|status| status.total_files == 0)
        .then(|| "There are no changes to discard.".to_string());
    let no_read = (!context.has_session).then(|| NO_REPOSITORY.to_string());
    let row = |action, label, reason, separated| MenuRow {
        action,
        label,
        reason,
        separated,
    };
    vec![
        row(
            MenuAction::Refresh,
            "Refresh",
            no_read.clone().or_else(|| {
                context
                    .reading
                    .then(|| "Source control is already refreshing.".to_string())
            }),
            false,
        ),
        row(MenuAction::DiscardAll, "Discard all…", write(no_changes), false),
        row(MenuAction::Fetch, "Fetch", write(fetch_reason(context)), true),
        row(MenuAction::Pull, "Pull", write(upstream(())), false),
        row(MenuAction::Push, "Push", write(upstream(())), false),
        row(MenuAction::AddRemote, "Add remote…", write(None), true),
        row(MenuAction::BranchDiff, "Branch diff", no_read.clone(), true),
        row(MenuAction::OpenHistory, "Open history", no_read, false),
    ]
}
pub fn publish_row(context: &MenuContext) -> PublishRow {
    let Some(status) = context.status else {
        return PublishRow::Hidden;
    };
    if status.has_upstream {
        return PublishRow::Hidden;
    }
    if let Some(reason) = blocked(context) {
        return PublishRow::Blocked(reason);
    }
    if status.branch.is_none() {
        return PublishRow::Blocked("Choose a branch before publishing.".into());
    }
    match context.remotes {
        None => PublishRow::Blocked(READING_REMOTES.into()),
        Some([]) => PublishRow::Blocked(
            "This repository has no configured remote. Add one before publishing.".into(),
        ),
        Some([one]) => PublishRow::One(one.clone()),
        Some(many) => PublishRow::Many(many.to_vec()),
    }
}
#[cfg(test)]
#[path = "actions_menu_tests.rs"]
mod tests;

//! The commit box's rules: when Stage All and Commit can run, what they say when they can't, and whether an amend asks
//! first. The panel draws them.
use super::git_status::{GitStatus, Section};

/// Broken by hand: the kit draws a placeholder without wrapping it, and this one is wider than the drawer.
pub const PLACEHOLDER: &str = "Message. Press Command-Enter to\ncommit.";
const STILL_RUNNING: &str = "Wait for the current source-control action to finish.";

pub fn commit_label(amend: bool) -> &'static str {
    if amend { "Amend" } else { "Commit" }
}
/// Commit takes something staged and a message. Amend needs neither: an empty box keeps the last message.
pub fn can_commit(status: Option<&GitStatus>, message: &str, amend: bool, busy: bool) -> bool {
    let Some(status) = status else {
        return false;
    };
    if busy {
        return false;
    }
    if amend {
        return status.head.is_some();
    }
    status.has_staged_files() && !message.trim().is_empty()
}
pub fn commit_hint(
    status: Option<&GitStatus>,
    message: &str,
    amend: bool,
    busy: bool,
) -> &'static str {
    if busy {
        return STILL_RUNNING;
    }
    if amend {
        return if message.trim().is_empty() {
            "Rewrite the last commit and keep its message. Do not amend a commit that is already pushed."
        } else {
            "Rewrite the last commit. Do not amend a commit that is already pushed and shared."
        };
    }
    if !status.is_some_and(GitStatus::has_staged_files) {
        return "Stage something first — a commit takes what is staged.";
    }
    if message.trim().is_empty() {
        return "Type a commit message first.";
    }
    "Commit the staged files"
}
/// What Stage All takes: the listed files with work git hasn't been told about, never a conflict, which staging would
/// mark resolved.
pub fn stageable(status: &GitStatus) -> Vec<String> {
    let mut paths: Vec<String> = [Section::Changes, Section::Untracked]
        .into_iter()
        .flat_map(|section| status.section(section))
        .filter(|(_, file)| !file.conflicted())
        .map(|(_, file)| file.path.clone())
        .collect();
    paths.sort();
    paths.dedup();
    paths
}
pub fn stage_hint(status: Option<&GitStatus>, busy: bool) -> String {
    if busy {
        return STILL_RUNNING.into();
    }
    let Some(status) = status else {
        return "Refresh Source control first.".into();
    };
    match stageable(status).len() {
        0 if status.total_files > status.files.len() => {
            "Only the files listed can be staged here; commit, stash or ignore files to see the rest.".into()
        }
        0 => "Everything is already staged.".into(),
        1 => "Stage the one changed file".into(),
        many => format!("Stage all {many} changed files"),
    }
}
/// Amending a commit that's already on its upstream asks first (g9#2).
pub fn amend_needs_confirm(status: &GitStatus, amend: bool) -> bool {
    amend && status.has_upstream && status.ahead == 0
}
#[cfg(test)]
#[path = "commit_box_tests.rs"]
mod tests;

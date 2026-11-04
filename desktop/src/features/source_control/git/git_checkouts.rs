//! What `git/checkouts` answers: the session's recorded folder and its other linked checkouts. The session's own row,
//! bare and prunable entries are already gone. Only this answer offers a checkout to read; anything else offers none.
use crate::core::socket::checkout_selector::{CheckoutSelector, folder_name};
use crate::core::socket::error_code::ErrorCode;
use serde::Deserialize;

#[derive(Clone, Debug, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct GitCheckouts {
    pub session_root: String,
    pub worktrees: Vec<GitCheckout>,
}
#[derive(Clone, Debug, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct GitCheckout {
    pub path: String,
    /// `None` when its head is detached.
    pub branch: Option<String>,
}
/// One line of a picker. `path` is `None` for the session's own folder, which is always sent as no selector.
#[derive(Clone, Debug, PartialEq)]
pub struct CheckoutChoice {
    pub path: Option<String>,
    pub label: String,
}
impl GitCheckouts {
    pub fn has_others(&self) -> bool {
        !self.worktrees.is_empty()
    }
    pub fn selector(&self, path: &str) -> Option<CheckoutSelector> {
        self.worktrees
            .iter()
            .any(|worktree| worktree.path == path)
            .then(|| CheckoutSelector {
                path: path.to_string(),
                expected_root: self.session_root.clone(),
            })
    }
    /// Whether this answer still offers what was picked from an earlier one.
    pub fn offers(&self, selector: &CheckoutSelector) -> bool {
        self.session_root == selector.expected_root
            && self.worktrees.iter().any(|worktree| worktree.path == selector.path)
    }
    /// Source control's picker: `name (session)`, then `name — branch`.
    pub fn scope_choices(&self) -> Vec<CheckoutChoice> {
        let others = self.worktrees.iter().map(|worktree| CheckoutChoice {
            path: Some(worktree.path.clone()),
            label: match worktree.branch.as_deref().map(str::trim).filter(|branch| !branch.is_empty()) {
                Some(branch) => format!("{} — {branch}", folder_name(&worktree.path)),
                None => folder_name(&worktree.path).to_string(),
            },
        });
        std::iter::once(CheckoutChoice {
            path: None,
            label: format!("{} (session)", folder_name(&self.session_root)),
        })
        .chain(others)
        .collect()
    }
    /// The history tab's picker: `name (branch)`. The session's own branch comes from its status.
    pub fn repository_choices(&self, own_branch: Option<&str>) -> Vec<CheckoutChoice> {
        std::iter::once(CheckoutChoice {
            path: None,
            label: repository_label(&self.session_root, own_branch),
        })
        .chain(self.worktrees.iter().map(|worktree| CheckoutChoice {
            path: Some(worktree.path.clone()),
            label: repository_label(&worktree.path, worktree.branch.as_deref()),
        }))
        .collect()
    }
}
pub fn repository_label(path: &str, branch: Option<&str>) -> String {
    match branch.map(str::trim).filter(|branch| !branch.is_empty()) {
        Some(branch) => format!("{} ({branch})", folder_name(path)),
        None => folder_name(path).to_string(),
    }
}
/// The checkout a read was pointed at can't be read any more, so the view goes back to the session's own.
pub fn checkout_gone(code: &ErrorCode) -> bool {
    matches!(code, ErrorCode::Refused { code }
        if matches!(code.as_str(), "checkout_unavailable" | "checkout_root_changed" | "source_changed"))
}
#[cfg(test)]
#[path = "git_checkouts_tests.rs"]
mod tests;

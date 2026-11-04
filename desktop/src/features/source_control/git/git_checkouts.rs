//! What `git/checkouts` answers: the session's recorded folder and its other linked checkouts. The session's own row,
//! bare and prunable entries are already gone. Only this answer offers a checkout to read; anything else offers none.
use crate::core::socket::checkout_selector::{CheckoutSelector, folder_name};
use crate::core::socket::error_code::ErrorCode;
use crate::core::socket::row_source::ExpectedRowSource;
use crate::core::state::workspace_panes::WorkspaceKey;
use gpui_kit::component::dock::PanelInfo;
use serde::{Deserialize, Serialize};

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
/// The longest checkout path the owner accepts.
const MAX_CHECKOUT_PATH: usize = 4096;
/// A checkout a panel was reading when its layout was saved, or that another panel sent it to. It names what to look
/// for and reads nothing: only a fresh answer that still offers that folder, for the same session and owner, makes it a
/// selector.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct CheckoutHint {
    pub session: String,
    pub source: ExpectedRowSource,
    pub path: String,
    pub expected_root: String,
}
impl CheckoutHint {
    pub fn new(session: &str, source: &ExpectedRowSource, selector: &CheckoutSelector) -> Self {
        Self {
            session: session.to_string(),
            source: source.clone(),
            path: selector.path.clone(),
            expected_root: selector.expected_root.clone(),
        }
    }
    /// The hint in a pane's saved layout. One that doesn't read is no hint.
    pub fn saved(info: &PanelInfo) -> Option<Self> {
        let PanelInfo::Panel(info) = info else {
            return None;
        };
        serde_json::from_value(info.get("checkoutHint")?.clone()).ok()
    }
    /// Whether a saved hint could have come from this workspace's tab. This admits the metadata and reads nothing.
    pub fn belongs_to(&self, key: &WorkspaceKey, pane_source: &ExpectedRowSource) -> bool {
        self.session == key.session_id
            && self.source.machine == key.machine
            && self.source == *pane_source
            && self.expected_root == key.checkout_root
            && !self.path.is_empty()
            && !self.path.contains('\0')
            && self.path.chars().count() <= MAX_CHECKOUT_PATH
    }
    pub fn selector(&self) -> CheckoutSelector {
        CheckoutSelector {
            path: self.path.clone(),
            expected_root: self.expected_root.clone(),
        }
    }
    pub fn qualified(
        &self,
        session: &str,
        source: &ExpectedRowSource,
        checkouts: Option<&GitCheckouts>,
    ) -> Option<CheckoutSelector> {
        let selector = self.selector();
        (self.session == session && self.source == *source && checkouts?.offers(&selector)).then_some(selector)
    }
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

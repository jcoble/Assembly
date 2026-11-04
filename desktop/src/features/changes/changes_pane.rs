//! The Changes tab beside Chat: the whole working copy stacked file by file, under the changed-file tree, or one
//! commit's file on its own. Replaces GitDiffView.svelte. It reads only while shown, and only the checkout Source
//! control has actually read for the selected session; reads for anything else stop as the selection moves.
use super::working_copy::{CombinedPatch, Scope, Selection, WorkingCopy};
use crate::core::intents::{open_commit_diff::OpenCommitDiff, open_diff::OpenDiff};
use crate::core::socket::{checkout_selector::CheckoutSelector, row_source::ExpectedRowSource};
use crate::core::state::{
    inspected_checkout::InspectedCheckout, preferences::Preferences,
    selected_session::SelectedSession, session_read::SessionRead,
};
use crate::core::ui::{center_panes::CenterPanes, changed_tree::ChangedTree};
use crate::features::source_control::git_status::GitStatus;
use crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator;
use gpui_kit::component::diff::{DiffMode, DiffState};
use gpui_kit::component::dock::{
    BasePanel, Panel, PanelEvent, PanelId, PanelInfo, PanelState, panel_handle, register_panel,
};
use gpui_kit::component::input::{EditorState, InputState};
use gpui_kit::*;
use serde_json::{Value, json};
use std::collections::{HashMap, HashSet};
use std::rc::Rc;

#[path = "changes_reads.rs"]
mod reads;
#[path = "changes_view.rs"]
mod view;

pub const CHANGES: &str = "Changes";
struct Open(WeakEntity<ChangesPane>);
impl Global for Open {}

#[derive(Clone, Debug, PartialEq)]
struct CommitPick {
    session: String,
    expected_source: Option<ExpectedRowSource>,
    sha: String,
    path: String,
    title: String,
    checkout: Option<CheckoutSelector>,
}
#[derive(Clone, Debug, PartialEq)]
enum Target {
    /// The checkout's changes, file by file.
    Stack,
    /// A commit's file, kept apart from the working copy and from whatever Source control moves on to.
    Commit(CommitPick),
}
#[derive(Clone, Debug, PartialEq)]
enum Picked {
    File(String),
    Commit(CommitPick),
}
struct CombinedView {
    path: String,
    patch: CombinedPatch,
    editor: Option<Entity<EditorState>>,
}

pub struct ChangesPane {
    focus: FocusHandle,
    target: Target,
    /// The session this tab saves itself under. A workspace owns the tab, so this doesn't follow the selection.
    owner: Option<(String, Option<ExpectedRowSource>)>,
    scope: Scope,
    mode: DiffMode,
    active: bool,
    status: Entity<SessionRead<GitStatus>>,
    /// The status answer the current round came from.
    started_for: Option<u64>,
    selection: Option<Selection>,
    working: WorkingCopy,
    /// Dropping one stops its read on the backend.
    reads: HashMap<String, Task<()>>,
    branch_read: Option<Task<()>>,
    base: Option<String>,
    branch_failure: Option<String>,
    commit_read: Option<Task<()>>,
    commit_waiting: bool,
    commit_failure: Option<String>,
    state: Entity<DiffState>,
    tree: ChangedTree,
    filter: String,
    filter_input: Option<(Entity<InputState>, Subscription)>,
    focus_path: Option<String>,
    active_file: Option<String>,
    viewed: HashSet<String>,
    /// The files showing a staged part and a working-tree part.
    both: Rc<HashSet<String>>,
    combined: Vec<CombinedView>,
    notes: Vec<(String, &'static str)>,
    totals: (usize, usize),
    more_line: Option<String>,
    /// What Source control last published that this tab has read since.
    published: Option<(String, u64)>,
    line_notice: Option<&'static str>,
    _heard: Vec<Subscription>,
}
impl ChangesPane {
    /// Validates saved target metadata before a workspace coordinator rebuilds this registered pane.
    /// Folder containment remains the existing backend SessionPaths authority.
    pub fn workspace_key(state: &PanelState, session: &str) -> Result<String, String> {
        let invalid = || "Invalid saved Changes target.".to_string();
        if state.panel_name.as_str() != CHANGES {
            return Err(invalid());
        }
        let PanelInfo::Panel(info) = &state.info else {
            return Err(invalid());
        };
        if info.is_null() {
            return Ok(CHANGES.into());
        }
        if !info.is_object() || info["session"].as_str() != Some(session) {
            return Err(invalid());
        }
        for field in ["path", "originalPath"] {
            if let Some(value) = info.get(field).filter(|value| !value.is_null()) {
                if value
                    .as_str()
                    .is_none_or(|path| path.is_empty() || path.contains('\0'))
                {
                    return Err(invalid());
                }
            }
        }
        if let Some(split) = info.get("split") {
            if !split.is_boolean() {
                return Err(invalid());
            }
        }
        if let Some(scope) = info.get("scope") {
            if scope.as_str().and_then(Scope::from_saved).is_none() {
                return Err(invalid());
            }
        }
        if let Some(sha) = info.get("sha") {
            if sha.as_str().is_none_or(|sha| {
                sha.len() != 40 || !sha.bytes().all(|byte| byte.is_ascii_hexdigit())
            }) || info["path"].as_str().is_none()
            {
                return Err(invalid());
            }
        }
        if let Some(source) = info
            .get("expectedSource")
            .filter(|source| !source.is_null())
        {
            if serde_json::from_value::<ExpectedRowSource>(source.clone()).is_err() {
                return Err(invalid());
            }
        }
        Ok(CHANGES.into())
    }
    pub fn register(cx: &mut App) {
        register_panel(cx, CHANGES, |build, _, cx| {
            let saved = match build.state().info.clone() {
                PanelInfo::Panel(saved) => saved,
                _ => Value::Null,
            };
            let pane = cx.new(|cx| {
                let mut pane = ChangesPane::new(None, cx);
                pane.restore(&saved, cx);
                pane
            });
            CenterPanes::remember(CHANGES, PanelId::from(pane.entity_id()), cx);
            cx.set_global(Open(pane.downgrade()));
            panel_handle(pane)
        });
        cx.on_action(|intent: &OpenDiff, cx| {
            let selected = SelectedSession::global(cx)
                .read(cx)
                .session_id()
                .map(String::from);
            if selected.as_deref() == Some(intent.session_id.as_str()) {
                Self::open_later(Picked::File(intent.path.clone()), cx);
            }
        });
        cx.on_action(|intent: &OpenCommitDiff, cx| {
            Self::open_later(
                Picked::Commit(CommitPick {
                    expected_source: intent.expected_source.clone(),
                    session: intent.session_id.clone(),
                    sha: intent.sha.clone(),
                    path: intent.path.clone(),
                    title: intent.title.clone(),
                    checkout: intent.checkout.clone(),
                }),
                cx,
            )
        });
    }
    fn open_later(picked: Picked, cx: &mut App) {
        if let Some(window) = cx
            .active_window()
            .or_else(|| cx.windows().into_iter().next())
        {
            cx.defer(move |cx| {
                _ = window.update(cx, |_, window, cx| Self::open(picked, window, cx));
            });
        }
    }
    fn open(picked: Picked, window: &mut Window, cx: &mut App) {
        if let Some(pane) = cx.try_global::<Open>().and_then(|open| open.0.upgrade()) {
            pane.update(cx, |pane, cx| pane.show(picked, cx));
            CenterPanes::open(CHANGES, window, cx, |_, _| pane);
        } else {
            CenterPanes::open(CHANGES, window, cx, |_, cx| {
                let pane = cx.new(|cx| ChangesPane::new(Some(picked), cx));
                cx.set_global(Open(pane.downgrade()));
                pane
            });
        }
        WorkspaceCoordinator::changed(cx);
    }
    fn new(picked: Option<Picked>, cx: &mut Context<Self>) -> Self {
        let status = cx.new(|cx| {
            let mut read = SessionRead::new("git", "status", cx).keyed("changes/git/status");
            read.refresh_on_settle(cx);
            read
        });
        let inspected = InspectedCheckout::global(cx);
        let mut heard = vec![
            cx.observe(&status, |pane, _, cx| pane.status_changed(cx)),
            cx.observe(&inspected, |pane, _, cx| pane.sync_inspected(true, cx)),
        ];
        heard.extend(
            Preferences::try_global(cx)
                .map(|preferences| cx.observe(&preferences, |_, _, cx| cx.notify())),
        );
        let mut pane = Self {
            focus: cx.focus_handle(),
            target: Target::Stack,
            owner: None,
            scope: Scope::default(),
            mode: DiffMode::Unified,
            active: false,
            status,
            started_for: None,
            selection: None,
            working: WorkingCopy::default(),
            reads: HashMap::new(),
            branch_read: None,
            base: None,
            branch_failure: None,
            commit_read: None,
            commit_waiting: false,
            commit_failure: None,
            state: cx.new(|cx| DiffState::new(Vec::new(), cx)),
            tree: ChangedTree::new(),
            filter: String::new(),
            filter_input: None,
            focus_path: None,
            active_file: None,
            viewed: HashSet::new(),
            both: Rc::new(HashSet::new()),
            combined: Vec::new(),
            notes: Vec::new(),
            totals: (0, 0),
            more_line: None,
            published: None,
            line_notice: None,
            _heard: heard,
        };
        if let Some(picked) = picked {
            pane.show(picked, cx);
        }
        pane
    }
    /// An old layout's target with no scope was the branch diff, or one file of the working copy.
    fn restore(&mut self, saved: &Value, cx: &mut Context<Self>) {
        if saved["split"] == true {
            self.mode = DiffMode::Split;
            self.state
                .update(cx, |state, cx| state.set_mode(DiffMode::Split, cx));
        }
        let text = |key: &str| saved[key].as_str().map(String::from);
        let Some(session) = text("session") else {
            return;
        };
        let source: Option<ExpectedRowSource> = saved
            .get("expectedSource")
            .cloned()
            .and_then(|value| serde_json::from_value(value).ok());
        self.owner = Some((session.clone(), source.clone()));
        if let (Some(sha), Some(path)) = (text("sha"), text("path")) {
            self.target = Target::Commit(CommitPick {
                session,
                expected_source: source,
                sha,
                path,
                title: text("title").unwrap_or_default(),
                checkout: None,
            });
            self.commit_waiting = true;
            return;
        }
        self.focus_path = text("path");
        self.scope = match text("scope").as_deref().and_then(Scope::from_saved) {
            Some(scope) => scope,
            None if self.focus_path.is_none() => Scope::Branch,
            None => Scope::Uncommitted,
        };
    }
    fn set_mode(&mut self, mode: DiffMode, cx: &mut Context<Self>) {
        self.mode = mode;
        self.state.update(cx, |state, cx| state.set_mode(mode, cx));
        CenterPanes::save_layout(cx);
        WorkspaceCoordinator::changed(cx);
        cx.notify();
    }
    fn saved_target(&self) -> Value {
        match &self.target {
            // Another checkout's diff isn't restored: only a fresh `git/checkouts` answer may offer a selector.
            Target::Commit(pick) if pick.checkout.is_some() => Value::Null,
            Target::Commit(pick) => json!({
                "session": pick.session, "sha": pick.sha, "path": pick.path, "title": pick.title,
                "expectedSource": pick.expected_source,
            }),
            Target::Stack => {
                let Some((session, source)) = &self.owner else {
                    return Value::Null;
                };
                let mut saved = json!({
                    "session": session, "expectedSource": source, "scope": self.scope.saved(),
                });
                let own_folder = self
                    .selection
                    .as_ref()
                    .is_none_or(|selection| selection.checkout.is_none());
                if let Some(path) = self
                    .focus_path
                    .as_ref()
                    .or(self.active_file.as_ref())
                    .filter(|_| own_folder)
                {
                    saved["path"] = json!(path);
                }
                saved
            }
        }
    }
}
impl EventEmitter<PanelEvent> for ChangesPane {}
impl Focusable for ChangesPane {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus.clone()
    }
}
impl BasePanel for ChangesPane {
    fn panel_name(&self) -> &'static str {
        CHANGES
    }
    fn set_active(&mut self, active: bool, _: &mut Window, cx: &mut Context<Self>) {
        self.activate(active, cx);
    }
    fn on_removed(&mut self, _: &mut Window, cx: &mut Context<Self>) {
        self.activate(false, cx);
    }
    fn dump(&self, _: &App) -> PanelState {
        let mut info = self.saved_target();
        if info.is_object() {
            info["split"] = json!(self.mode == DiffMode::Split);
        }
        PanelState {
            panel_name: CHANGES.into(),
            children: Vec::new(),
            info: PanelInfo::panel(info),
        }
    }
}
impl Panel for ChangesPane {
    fn title(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        CHANGES
    }
}
#[cfg(test)]
#[path = "changes_restart_real_tests.rs"]
mod restart_real_tests;
#[cfg(test)]
#[path = "changes_saved_tests.rs"]
mod saved_tests;
#[cfg(test)]
#[path = "changes_pane_tests.rs"]
mod tests;

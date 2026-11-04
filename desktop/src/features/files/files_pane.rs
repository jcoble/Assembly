//! `FilesPane` draws the selected session's folder as a tree (`FileTree`), loading a folder with `files/list` the first
//! time it opens. Each load is a task the pane holds by folder, so closing the folder, switching sessions or closing the
//! pane drops it, which cancels the call. A hidden pane loads nothing until it's shown. It registers under the Files
//! pane's name, after the placeholders.

use std::collections::HashMap;

use gpui_kit::component::dock::{BasePanel, Panel, PanelEvent, panel_handle, register_panel};
use gpui_kit::*;
use serde_json::json;

#[path = "files_pane_actions.rs"]
mod actions;
#[path = "files_pane_render.rs"]
mod render;
#[path = "files_pane_workspace.rs"]
mod workspace;

use super::entry::{FileListing, ObservedRoot, PageCursor};
use super::file_tree::{FileTree, Row};
use super::file_view::FileView;
use crate::core::socket::client::SocketClient;
use crate::core::socket::outcome::Outcome;
use crate::core::state::selected_session::SelectedSession;
use crate::core::ui::pane_names::FILES;

pub const TREE_LINE: Pixels = px(26.);
/// How far each level of the tree steps in.
const INDENT: Pixels = px(14.);

pub struct FilesPane {
    focus: FocusHandle,
    /// The session whose folder the tree shows.
    session: Option<String>,
    source: Option<crate::core::socket::row_source::ExpectedRowSource>,
    owner: Option<super::entry::FileOwnership>,
    pub(super) tree: FileTree,
    /// Whether the tree has asked for this session's root yet: only once the pane is shown.
    started: bool,
    shown: bool,
    generation: u64,
    include_excluded: bool,
    descending: bool,
    lease: Option<String>,
    observing: Option<Task<()>>,
    pub(super) selected: Option<String>,
    revealing: Option<String>,
    workspace_key: Option<crate::core::state::workspace_panes::WorkspaceKey>,
    workspace_checked: bool,
    restoring_tree: Option<workspace::TreeRestore>,
    /// Each folder's load, by folder; dropping one cancels its call.
    pub(super) loads: HashMap<String, Task<()>>,
    list: UniformListScrollHandle,
    action_event: Option<Subscription>,
    action_loading: Option<Task<()>>,
    action_error: Option<String>,
    _heard: Subscription,
}

impl FilesPane {
    pub fn register(cx: &mut App) {
        crate::core::state::workspace_panes::WorkspacePanes::register(
            Box::new(super::tree_codec::TreeCodec),
            cx,
        )
        .expect("one tree codec registration");
        register_panel(cx, FILES, |_, _, cx| panel_handle(cx.new(FilesPane::new)));
    }

    pub fn new(cx: &mut Context<Self>) -> Self {
        let heard = cx.observe(&SelectedSession::global(cx), |pane, _, cx| pane.follow(cx));
        let mut pane = FilesPane {
            focus: cx.focus_handle(),
            session: None,
            source: None,
            owner: None,
            tree: FileTree::default(),
            started: false,
            shown: false,
            generation: 0,
            include_excluded: true,
            descending: false,
            lease: None,
            observing: None,
            selected: None,
            revealing: None,
            workspace_key: None,
            workspace_checked: false,
            restoring_tree: None,
            loads: HashMap::new(),
            list: UniformListScrollHandle::new(),
            action_event: None,
            action_loading: None,
            action_error: None,
            _heard: heard,
        };
        pane.follow(cx);
        pane
    }

    /// Takes the selected session: a new one drops the old tree and its loads, and loads its root if the pane is shown.
    fn follow(&mut self, cx: &mut Context<Self>) {
        let session = SelectedSession::global(cx)
            .read(cx)
            .session_id()
            .map(String::from);
        let source = SelectedSession::global(cx).read(cx).source().cloned();
        if session == self.session && source == self.source {
            return;
        }
        self.release_watch(cx);
        self.action_loading = None;
        self.action_error = None;
        self.generation = self.generation.wrapping_add(1);
        self.session = session;
        self.source = source;
        self.owner = None;
        self.workspace_key = None;
        self.workspace_checked = false;
        self.restoring_tree = None;
        self.tree = FileTree::default();
        self.loads.clear();
        self.selected = None;
        self.revealing = None;
        self.started = false;
        self.start(cx);
        cx.notify();
    }

    fn start(&mut self, cx: &mut Context<Self>) {
        if !self.shown || self.session.is_none() {
            return;
        }
        if crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator::try_global(cx)
            .is_some()
            && crate::core::state::workspace_panes::WorkspacePanes::key(cx).is_none()
        {
            return;
        }
        if !self.validate_workspace_owner(cx) {
            return;
        }
        if self.lease.is_none() {
            if self.observing.is_some() {
                return;
            }
            let Some(socket) = SocketClient::try_global(cx) else {
                return;
            };
            let session = self.session.clone().expect("selected session");
            let generation = self.generation;
            let call = socket.read(cx).call::<ObservedRoot>(
                "files",
                "observe",
                json!({ "session": session, "expectedSource": self.source, "expectedOwner": self.owner }),
                None,
            );
            self.observing = Some(cx.spawn(async move |pane, cx| {
                let answer = call.await;
                let _ = pane.update(cx, |pane, cx| {
                    if pane.generation != generation || !pane.shown {
                        return;
                    }
                    pane.observing = None;
                    match answer {
                        Outcome::Ok(observed) => {
                            pane.lease = Some(observed.lease);
                            pane.start(cx);
                        }
                        Outcome::Failed(error) => {
                            pane.tree.start();
                            pane.tree.loaded("", Err(error.message));
                        }
                        Outcome::Aborted => {
                            pane.tree.start();
                            pane.tree.loaded(
                                "",
                                Err("The connection dropped; retry this folder.".into()),
                            );
                        }
                    }
                    cx.notify();
                });
            }));
            return;
        }
        if !self.started || self.tree.root_failed() {
            self.started = true;
            self.tree.start();
            self.load(String::new(), None, cx);
        }
    }

    fn refresh(&mut self, cx: &mut Context<Self>) {
        self.generation = self.generation.wrapping_add(1);
        self.loads.clear();
        self.observing = None;
        self.tree = FileTree::default();
        self.started = false;
        self.start(cx);
        cx.notify();
    }

    pub(super) fn is_loading(&self) -> bool {
        self.observing.is_some() || !self.loads.is_empty()
    }

    fn release_watch(&mut self, cx: &mut Context<Self>) {
        self.observing = None;
        if let (Some(lease), Some(socket)) = (self.lease.take(), SocketClient::try_global(cx)) {
            let call = socket.read(cx).call::<serde_json::Value>(
                "files",
                "unobserve",
                json!({ "lease": lease }),
                None,
            );
            call.ignore_reply();
        }
    }

    fn load(&mut self, dir: String, cursor: Option<PageCursor>, cx: &mut Context<Self>) {
        let (Some(session), Some(socket)) = (self.session.clone(), SocketClient::try_global(cx))
        else {
            self.tree
                .loaded(&dir, Err("The backend isn't running.".into()));
            return;
        };
        if !self.shown {
            return;
        }
        let generation = self.generation;
        let args = json!({ "session": session, "dir": dir, "includeExcluded": self.include_excluded, "descending": self.descending, "expectedSource": self.source, "expectedOwner": self.owner, "cursor": cursor });
        let call = socket
            .read(cx)
            .call::<FileListing>("files", "list", args, None);
        let at = dir.clone();
        let task = cx.spawn(async move |pane, cx| {
            let outcome = call.await;
            let _ = pane.update(cx, |pane, cx| {
                if pane.generation != generation || !pane.shown {
                    return;
                }
                pane.loads.remove(&at);
                let answer = match outcome {
                    Outcome::Ok(listing) => Ok(listing),
                    Outcome::Failed(failure) if failure.message.contains("cursor_changed") => {
                        pane.started = false;
                        pane.loads.clear();
                        pane.start(cx);
                        return;
                    }
                    Outcome::Failed(failure) => Err(failure.message),
                    Outcome::Aborted => {
                        Err("The connection dropped; open it again to retry.".into())
                    }
                };
                pane.tree.loaded(&at, answer);
                pane.continue_reveal(cx);
                pane.continue_workspace_tree(cx);
                cx.notify();
            });
        });
        self.loads.insert(dir, task);
    }

    pub fn reveal_folder(session: &str, path: &str, source: Option<&crate::core::socket::row_source::ExpectedRowSource>, window: &mut Window, cx: &mut App) {
        use crate::core::ui::center_panes::CenterPanes;
        use gpui_kit::component::dock::{DockPlacement, PaneRef, PanelHandle};
        if SelectedSession::global(cx).read(cx).session_id() != Some(session) {
            return;
        }
        let Some(dock) = CenterPanes::dock(cx) else {
            return;
        };
        let mut found = None;
        for placement in [
            DockPlacement::Left,
            DockPlacement::Center,
            DockPlacement::Right,
            DockPlacement::Bottom,
        ] {
            if let Some(tree) = dock.read(cx).layout(placement) {
                tree.root().walk(&mut |node| {
                    if let PaneRef::Tabs { panels, .. } = node.kind() {
                        for id in panels {
                            if let Some(panel) = dock.read(cx).panel(*id)
                                && let Some(handle) = PanelHandle::of(panel)
                                && let Ok(view) = handle.view().downcast::<Self>()
                            {
                                found = Some((*id, placement, view));
                            }
                        }
                    }
                });
            }
        }
        let Some((id, placement, view)) = found else {
            return;
        };
        view.update(cx, |pane, cx| {
            if pane.source.as_ref() != source {
                pane.action_error=Some("The search result belongs to an earlier file source.".into());cx.notify();return;
            }
            pane.revealing = Some(path.to_string());
            pane.continue_reveal(cx);
        });
        dock.update(cx, |dock, cx| {
            dock.select_panel(id, window, cx);
            if placement != DockPlacement::Center && !dock.is_dock_open(placement) {
                dock.toggle_dock(placement, window, cx);
            }
        });
    }

    fn continue_reveal(&mut self, cx: &mut Context<Self>) {
        use super::file_tree::RevealStep;
        let Some(path) = self.revealing.clone() else {
            return;
        };
        if !self.shown || !self.started {
            return;
        }
        match self.tree.reveal(&path) {
            RevealStep::Load { dir, cursor } => self.load(dir, cursor, cx),
            RevealStep::Waiting => {}
            RevealStep::Ready => {
                self.selected = Some(path.clone());
                self.revealing = None;
                if let Some(ix) =
                    self.tree.rows().iter().position(
                        |row| matches!(row, Row::Entry { entry, .. } if entry.path == path),
                    )
                {
                    self.list.scroll_to_item(ix, ScrollStrategy::Center);
                }
            }
            RevealStep::Missing => {
                self.revealing = None;
            }
        }
        cx.notify();
    }

    /// A click on a row selects it, and opens or closes a folder.
    pub(super) fn clicked(&mut self, path: String, folder: bool, cx: &mut Context<Self>) {
        if folder {
            match self.tree.toggle(&path) {
                Some(dir) => self.load(dir, None, cx),
                None => {
                    let prefix = path.clone() + "/";
                    self.loads
                        .retain(|dir, _| dir != &path && !dir.starts_with(&prefix));
                }
            }
        }
        self.selected = Some(path);
        self.restoring_tree = None;
        crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator::changed(cx);
        cx.notify();
    }

    fn key_down(&mut self, event: &KeyDownEvent, window: &mut Window, cx: &mut Context<Self>) {
        match event.keystroke.key.as_str() {
            "up" | "down" => {
                if let Some((path, _, ix)) = self.tree.adjacent(
                    self.selected.as_deref(),
                    if event.keystroke.key == "up" { -1 } else { 1 },
                ) {
                    self.selected = Some(path);
                    self.list.scroll_to_item(ix, ScrollStrategy::Center);
                }
            }
            "enter" | "right" => {
                if let Some(path) = self.selected.clone()
                    && let Some((folder, open)) = self.tree.entry_state(&path)
                {
                    if folder && !open {
                        self.clicked(path, true, cx);
                    } else if !folder
                        && event.keystroke.key == "enter"
                        && let Some(session) = &self.session
                    {
                        FileView::open_at(session.clone(), path, None, None, true, self.source.clone(), window, cx);
                    }
                }
            }
            "left" => {
                if let Some(path) = self.selected.clone() {
                    if self.tree.entry_state(&path) == Some((true, true)) {
                        self.clicked(path, true, cx);
                    } else if let Some((parent, _)) = path.rsplit_once('/') {
                        self.selected = Some(parent.to_string());
                    }
                }
            }
            "f2" => {
                if let Some(path) = self.selected.clone() {
                    self.open_action("rename", path, None, None, window, cx);
                }
            }
            _ => return,
        }
        crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator::changed(cx);
        cx.stop_propagation();
        cx.notify();
    }
}

impl EventEmitter<PanelEvent> for FilesPane {}

impl Focusable for FilesPane {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus.clone()
    }
}

impl BasePanel for FilesPane {
    fn panel_name(&self) -> &'static str {
        FILES
    }

    /// The left column's tabs are permanent.
    fn closable(&self, _cx: &App) -> bool {
        false
    }

    /// Shown, it loads the session's root if it hasn't yet, or if that failed; hidden, it keeps what it has.
    fn set_active(&mut self, active: bool, _window: &mut Window, cx: &mut Context<Self>) {
        self.shown = active;
        if !active {
            self.workspace_checked = false;
            self.generation = self.generation.wrapping_add(1);
            self.loads.clear();
            self.release_watch(cx);
            self.action_loading = None;
            self.started = false;
        } else {
            self.start(cx);
            self.continue_reveal(cx);
        }
        cx.notify();
    }
}

impl Panel for FilesPane {
    fn title(&mut self, _window: &mut Window, _cx: &mut Context<Self>) -> impl IntoElement {
        FILES
    }
}

//! `FilesPane` draws the selected session's folder as a tree (`FileTree`), loading a folder with `files/list` the first
//! time it opens. Each load is a task the pane holds by folder, so closing the folder, switching sessions or closing the
//! pane drops it, which cancels the call. A hidden pane loads nothing until it's shown. It registers under the Files
//! pane's name, after the placeholders.

use std::collections::HashMap;

use gpui_kit::component::dock::{BasePanel, Panel, PanelEvent, panel_handle, register_panel};
use gpui_kit::component::input::InputState;
use gpui_kit::*;
use serde_json::json;

#[path = "files_pane_actions.rs"]
mod actions;
#[path = "files_pane_entry.rs"]
mod entry_bar;
#[path = "files_pane_filter.rs"]
mod filter;
#[path = "files_pane_header.rs"]
mod header;
#[path = "file_history_card.rs"]
mod history_card;
#[path = "files_pane_render.rs"]
mod render;
#[path = "files_pane_root.rs"]
mod root;
#[path = "files_pane_run.rs"]
mod run;
#[path = "files_pane_workspace.rs"]
mod workspace;

use self::root::plain_failure;
use super::entry::{FileListing, ObservedRoot, PageCursor};
use super::file_tree::{FileTree, Row};
use super::file_view::FileView;
use crate::core::socket::call_failure::CallFailure;
use crate::core::socket::client::SocketClient;
use crate::core::socket::outcome::Outcome;
use crate::core::state::selected_session::SelectedSession;
use crate::core::ui::pane_names::FILES;

pub const TREE_LINE: Pixels = px(28.);
/// How far each level of the tree steps in.
const INDENT: Pixels = px(16.);

type Subject = (
    Option<String>,
    Option<crate::core::socket::row_source::ExpectedRowSource>,
);

pub struct FilesPane {
    focus: FocusHandle,
    /// The session whose folder the tree shows.
    session: Option<String>,
    source: Option<crate::core::socket::row_source::ExpectedRowSource>,
    owner: Option<super::entry::FileOwnership>,
    pub(super) tree: FileTree,
    /// The next tree while a reload walks the folder; `tree` stays on screen until this is ready.
    building: Option<FileTree>,
    /// A reload was asked for while one was building.
    reload_again: bool,
    /// Showing the pane again reloads what it kept, once its owner and lease are back.
    reload_when_ready: bool,
    /// Why the root can't be listed, with its code, which the rows' text alone wouldn't carry.
    root_failure: Option<CallFailure>,
    /// Held only while waiting for a folder its first message will make.
    first_message: Option<Subscription>,
    /// Whether the tree has asked for this session's root yet: only once the pane is shown.
    started: bool,
    shown: bool,
    generation: u64,
    include_excluded: bool,
    descending: bool,
    lease: Option<String>,
    observing: Option<Task<()>>,
    pub(super) selected: Option<String>,
    /// A path to show in the tree, and whether it's a folder, which opens once shown.
    revealing: Option<(String, bool)>,
    workspace_key: Option<crate::core::state::workspace_panes::WorkspaceKey>,
    workspace_checked: bool,
    restoring_tree: Option<workspace::TreeRestore>,
    /// Each folder's load, by folder; dropping one cancels its call.
    pub(super) loads: HashMap<String, Task<()>>,
    list: UniformListScrollHandle,
    /// The file action running, one at a time.
    action: super::file_actions::ActionRun,
    /// The entry bar, while something is being named.
    pub(super) entry: Option<entry_bar::OpenEntry>,
    /// The action itself. Hiding or switching leaves it to finish; only the pane going away drops it.
    action_task: Option<Task<()>>,
    action_loading: Option<Task<()>>,
    pub(super) action_error: Option<String>,
    pub(super) filter: Entity<super::file_filter::FileFilter>,
    /// The filter's field and what it heard, made on the first draw, which has the window.
    filter_input: Option<(Entity<InputState>, Subscription)>,
    /// The field empties on the next draw: another folder starts with no filter.
    clear_filter_input: bool,
    filter_list: UniformListScrollHandle,
    /// The file history card's list, its drop-down, and which file it follows.
    pub(super) history: Entity<super::file_history::FileHistoryList>,
    history_drops: Entity<crate::core::ui::row_drop_down::RowDropDowns>,
    history_target: super::history_target::HistoryTarget,
    _history_heard: [Subscription; 3],
    _heard: [Subscription; 4],
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
        let viewed = crate::core::state::viewed_project::ViewedProject::global(cx);
        let heard = cx.observe(&SelectedSession::global(cx), |pane, _, cx| pane.follow(cx));
        let heard_viewed = cx.observe(&viewed, |pane, _, cx| pane.follow(cx));
        let touched = crate::core::state::folder_touched::FolderTouched::global(cx);
        let heard_touched = cx.observe(&touched, |pane, touched, cx| {
            if pane.session.is_some() && touched.read(cx).session() == pane.session.as_deref() {
                pane.reload_kept(cx);
            }
        });
        let filter = cx.new(|_| super::file_filter::FileFilter::default());
        let heard_filter = cx.observe(&filter, |_, _, cx| cx.notify());
        let history = cx.new(super::file_history::FileHistoryList::new);
        let history_drops = cx.new(|_| crate::core::ui::row_drop_down::RowDropDowns::default());
        let history_heard = Self::history_heard(&history_drops, cx);
        let mut pane = FilesPane {
            focus: cx.focus_handle(),
            session: None,
            source: None,
            owner: None,
            tree: FileTree::default(),
            building: None,
            reload_again: false,
            reload_when_ready: false,
            root_failure: None,
            first_message: None,
            started: false,
            shown: false,
            generation: 0,
            include_excluded: false,
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
            action: Default::default(),
            entry: None,
            action_task: None,
            action_loading: None,
            action_error: None,
            filter,
            filter_input: None,
            clear_filter_input: false,
            filter_list: UniformListScrollHandle::new(),
            history,
            history_drops,
            history_target: Default::default(),
            _history_heard: history_heard,
            _heard: [heard, heard_viewed, heard_touched, heard_filter],
        };
        pane.follow(cx);
        pane
    }

    /// Takes the selected session: a new one drops the old tree and its loads, and loads its root if the pane is shown.
    fn follow(&mut self, cx: &mut Context<Self>) {
        let subject = Self::subject(cx);
        if subject.0 == self.session && subject.1 == self.source {
            return;
        }
        self.take_subject(subject, cx);
    }

    fn subject(cx: &App) -> Subject {
        match crate::core::state::viewed_project::ViewedProject::subject(cx) {
            Some((session, source)) => (Some(session), source),
            None => (None, None),
        }
    }

    /// Starts over on this session: the old tree, its loads and its lease go.
    fn take_subject(&mut self, (session, source): Subject, cx: &mut Context<Self>) {
        self.release_watch(cx);
        self.action_loading = None;
        self.action_error = None;
        self.entry = None;
        self.generation = self.generation.wrapping_add(1);
        self.session = session;
        self.source = source;
        self.owner = None;
        self.workspace_key = None;
        self.workspace_checked = false;
        self.restoring_tree = None;
        self.drop_reload();
        self.clear_root_failure();
        self.tree = FileTree::default();
        self.loads.clear();
        self.selected = None;
        self.revealing = None;
        self.started = false;
        self.clear_filter(cx);
        self.sync_filter(cx);
        self.history_target = Default::default();
        self.follow_active_file(cx);
        self.start(cx);
        cx.notify();
    }

    fn start(&mut self, cx: &mut Context<Self>) {
        if !self.shown || self.session.is_none() {
            return;
        }
        if crate::core::state::workspace_panes::WorkspacePanes::awaiting_key(cx) {
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
                        Outcome::Failed(error) => pane.fail_root(error, cx),
                        Outcome::Aborted => pane.fail_root(
                            plain_failure("The connection dropped; retry this folder."),
                            cx,
                        ),
                    }
                    cx.notify();
                });
            }));
            return;
        }
        if !self.started || self.tree.root_failed() {
            self.started = true;
            self.clear_root_failure();
            if std::mem::take(&mut self.reload_when_ready) && self.tree.root_loaded() {
                self.reload_kept(cx);
                return;
            }
            self.tree.start();
            self.load(String::new(), None, cx);
        }
    }

    pub(super) fn is_loading(&self) -> bool {
        self.observing.is_some() || !self.loads.is_empty() || self.building.is_some()
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

    pub(super) fn load(&mut self, dir: String, cursor: Option<PageCursor>, cx: &mut Context<Self>) {
        let (Some(session), Some(socket)) = (self.session.clone(), SocketClient::try_global(cx))
        else {
            self.fail_folder(&dir, plain_failure("The backend isn't running."), cx);
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
                match outcome {
                    Outcome::Ok(listing) => pane.walked_tree().loaded(&at, Ok(listing)),
                    Outcome::Failed(failure) if failure.message.contains("cursor_changed") => {
                        pane.restart_reload(cx);
                        return;
                    }
                    Outcome::Failed(failure) => pane.fail_folder(&at, failure, cx),
                    Outcome::Aborted => pane.fail_folder(
                        &at,
                        plain_failure("The connection dropped; open it again to retry."),
                        cx,
                    ),
                }
                pane.continue_reveal(cx);
                pane.continue_workspace_tree(cx);
                pane.sync_history(cx);
                cx.notify();
            });
        });
        self.loads.insert(dir, task);
    }

    fn continue_reveal(&mut self, cx: &mut Context<Self>) {
        use super::file_tree::RevealStep;
        let Some((path, folder)) = self.revealing.clone() else {
            return;
        };
        if !self.shown || !self.started || self.building.is_some() {
            return;
        }
        let step = if folder {
            self.tree.reveal(&path)
        } else {
            self.tree.reveal_entry(&path)
        };
        match step {
            RevealStep::Load { dir, cursor } => self.load(dir, cursor, cx),
            RevealStep::Waiting => {}
            RevealStep::Ready => {
                self.selected = Some(path.clone());
                self.revealing = None;
                self.sync_history(cx);
                let rows = self.tree.visible(0..self.tree.row_count());
                if let Some(ix) = rows
                    .iter()
                    .position(|row| matches!(row, Row::Entry { entry, .. } if entry.path == path))
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
        // A folder clicked during a reload joins it: the walk starts over from what the click left.
        let rebuilding = folder && self.building.take().is_some();
        if rebuilding {
            self.generation = self.generation.wrapping_add(1);
            self.loads.clear();
            self.tree.cancel_loads();
        }
        if self.building.is_none() {
            self.restoring_tree = None;
        }
        if folder {
            match self.tree.toggle(&path) {
                Some(dir) => {
                    if !rebuilding {
                        self.load(dir, None, cx)
                    }
                }
                None => {
                    let prefix = path.clone() + "/";
                    self.loads
                        .retain(|dir, _| dir != &path && !dir.starts_with(&prefix));
                }
            }
        }
        self.selected = Some(path);
        if rebuilding {
            self.reload_kept(cx);
        }
        self.sync_history(cx);
        crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator::changed(cx);
        cx.notify();
    }

    fn key_down(&mut self, event: &KeyDownEvent, window: &mut Window, cx: &mut Context<Self>) {
        // Keys typed in the filter or the entry bar are theirs.
        if !self.focus.is_focused(window) {
            return;
        }
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
                        FileView::open_at(
                            session.clone(),
                            path,
                            (None, None),
                            true,
                            self.source.clone(),
                            window,
                            cx,
                        );
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
                    self.start_entry(super::file_entry_bar::EntryKind::Rename, &path, window, cx);
                }
            }
            _ => return,
        }
        self.sync_history(cx);
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
        self.filter
            .update(cx, |filter, cx| filter.set_shown(active, cx));
        if !active {
            self.workspace_checked = false;
            self.generation = self.generation.wrapping_add(1);
            self.loads.clear();
            self.release_watch(cx);
            self.action_loading = None;
            self.started = false;
            self.tree.cancel_loads();
            if self.building.is_some() {
                self.restoring_tree = None;
            }
            self.drop_reload();
        } else {
            self.reload_when_ready = self.tree.root_loaded();
            self.start(cx);
            self.continue_reveal(cx);
        }
        self.sync_history(cx);
        cx.notify();
    }
}

impl Panel for FilesPane {
    fn title(&mut self, _window: &mut Window, _cx: &mut Context<Self>) -> impl IntoElement {
        FILES
    }
}

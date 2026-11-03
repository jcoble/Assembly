//! `FilesPane` draws the selected session's folder as a tree (`FileTree`), loading a folder with `files/list` the first
//! time it opens. Each load is a task the pane holds by folder, so closing the folder, switching sessions or closing the
//! pane drops it, which cancels the call. A hidden pane loads nothing until it's shown. It registers under the Files
//! pane's name, after the placeholders.

use std::collections::HashMap;

use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::dock::{BasePanel, Panel, PanelEvent, panel_handle, register_panel};
use gpui_kit::component::tooltip::Tooltip;
use gpui_kit::component::{ActiveTheme as _, Icon, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use serde_json::json;

use super::entry::{FileListing, ObservedRoot, PageCursor};
use super::file_tree::{FileTree, Row};
use super::file_view::FileView;
use crate::core::socket::client::SocketClient;
use crate::core::socket::outcome::Outcome;
use crate::core::state::selected_session::SelectedSession;
use crate::core::ui::pane_names::FILES;

/// The design system's `tree` type: 15 px text on 26 px lines.
pub const TREE_TEXT: Pixels = px(15.);
pub const TREE_LINE: Pixels = px(26.);
/// How far each level of the tree steps in.
const INDENT: Pixels = px(14.);

pub struct FilesPane {
    focus: FocusHandle,
    /// The session whose folder the tree shows.
    session: Option<String>,
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
    /// Each folder's load, by folder; dropping one cancels its call.
    pub(super) loads: HashMap<String, Task<()>>,
    list: UniformListScrollHandle,
    _heard: Subscription,
}

impl FilesPane {
    pub fn register(cx: &mut App) {
        register_panel(cx, FILES, |_, _, cx| panel_handle(cx.new(FilesPane::new)));
    }

    pub fn new(cx: &mut Context<Self>) -> Self {
        let heard = cx.observe(&SelectedSession::global(cx), |pane, _, cx| pane.follow(cx));
        let mut pane = FilesPane {
            focus: cx.focus_handle(),
            session: None,
            tree: FileTree::default(),
            started: false,
            shown: false,
            generation: 0,
            include_excluded: true,
            descending: false,
            lease: None,
            observing: None,
            selected: None,
            loads: HashMap::new(),
            list: UniformListScrollHandle::new(),
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
        if session == self.session {
            return;
        }
        self.release_watch(cx);
        self.generation = self.generation.wrapping_add(1);
        self.session = session;
        self.tree = FileTree::default();
        self.loads.clear();
        self.selected = None;
        self.started = false;
        self.start(cx);
        cx.notify();
    }

    fn start(&mut self, cx: &mut Context<Self>) {
        if !self.shown || self.session.is_none() {
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
                json!({ "session": session }),
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
        let args = json!({ "session": session, "dir": dir, "includeExcluded": self.include_excluded, "descending": self.descending, "cursor": cursor });
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
                cx.notify();
            });
        });
        self.loads.insert(dir, task);
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
        cx.notify();
    }

    fn row(&self, row: Row<'_>, ix: usize, cx: &mut Context<Self>) -> AnyElement {
        let theme = cx.theme();
        let (muted, danger) = (theme.muted_foreground, theme.danger);
        let line = |depth: usize| {
            h_flex()
                .h(TREE_LINE)
                .pl(px(8.) + INDENT * depth as f32)
                .pr_2()
                .gap_1()
                .text_size(TREE_TEXT)
                .line_height(TREE_LINE)
                .whitespace_nowrap()
                .overflow_hidden()
        };
        match row {
            Row::Entry { entry, depth, open } => {
                let picked = self.selected.as_deref() == Some(entry.path.as_str());
                let (path, folder) = (entry.path.clone(), entry.is_directory);
                let full_path = path.clone();
                let (accent, secondary, foreground) =
                    (theme.accent, theme.secondary, theme.foreground);
                let glyph = match (folder, open) {
                    (true, true) => IconName::FolderOpen,
                    (true, false) => IconName::Folder,
                    _ => IconName::File,
                };
                line(depth)
                    .id(ix)
                    .rounded(px(6.))
                    .cursor_pointer()
                    .text_color(if entry.excluded { muted } else { foreground })
                    .when(picked, |row| row.bg(secondary))
                    .when(!picked, |row| row.hover(|row| row.bg(accent)))
                    .child(div().w(px(12.)).flex_none().when(folder, |chevron| {
                        chevron.child(
                            Icon::new(if open {
                                IconName::ChevronDown
                            } else {
                                IconName::ChevronRight
                            })
                            .size(px(12.))
                            .text_color(muted),
                        )
                    }))
                    .child(Icon::new(glyph).size(px(14.)).text_color(muted))
                    .child(
                        div()
                            .min_w_0()
                            .overflow_hidden()
                            .text_ellipsis()
                            .child(entry.name.clone()),
                    )
                    .tooltip(move |window, cx| Tooltip::new(full_path.clone()).build(window, cx))
                    .on_click(cx.listener(move |pane, event: &ClickEvent, window, cx| {
                        pane.clicked(path.clone(), folder, cx);
                        if let (false, Some(session)) = (folder, pane.session.clone()) {
                            FileView::open_at(
                                session,
                                path.clone(),
                                None,
                                None,
                                event.click_count() > 1,
                                window,
                                cx,
                            );
                        }
                    }))
                    .into_any_element()
            }
            Row::Loading { depth } => line(depth)
                .text_color(muted)
                .child("Loading…")
                .into_any_element(),
            Row::Failed { depth, message } => line(depth)
                .text_color(danger)
                .child(
                    div()
                        .overflow_hidden()
                        .text_ellipsis()
                        .child(message.to_string()),
                )
                .into_any_element(),
            Row::Truncated { depth, dir } => {
                let dir = dir.to_owned();
                line(depth)
                    .id(ix)
                    .text_color(muted)
                    .cursor_pointer()
                    .child("Load more")
                    .on_click(cx.listener(move |pane, _, _, cx| {
                        if let Some(cursor) = pane.tree.more(&dir) {
                            pane.load(dir.clone(), Some(cursor), cx);
                        }
                    }))
                    .into_any_element()
            }
        }
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
            self.generation = self.generation.wrapping_add(1);
            self.loads.clear();
            self.release_watch(cx);
            self.started = false;
        } else {
            self.start(cx);
        }
        cx.notify();
    }
}

impl Panel for FilesPane {
    fn title(&mut self, _window: &mut Window, _cx: &mut Context<Self>) -> impl IntoElement {
        FILES
    }
}

impl Render for FilesPane {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let muted = cx.theme().muted_foreground;
        let pane = v_flex().track_focus(&self.focus).size_full();
        if self.session.is_none() {
            return pane.child(
                div()
                    .px_3()
                    .py_2()
                    .text_sm()
                    .text_color(muted)
                    .child("Pick a session to see its files."),
            );
        }
        let count = self.tree.rows().len();
        pane.child(
            h_flex()
                .px_2()
                .py_1()
                .gap_1()
                .child(
                    Button::new("files-excluded")
                        .ghost()
                        .label(if self.include_excluded {
                            "Hide excluded"
                        } else {
                            "Show excluded"
                        })
                        .on_click(cx.listener(|pane, _, _, cx| {
                            pane.include_excluded = !pane.include_excluded;
                            pane.refresh(cx);
                        })),
                )
                .child(
                    Button::new("files-sort")
                        .ghost()
                        .label(if self.descending { "Z–A" } else { "A–Z" })
                        .on_click(cx.listener(|pane, _, _, cx| {
                            pane.descending = !pane.descending;
                            pane.refresh(cx);
                        })),
                )
                .child(
                    Button::new("files-refresh")
                        .ghost()
                        .label("Refresh")
                        .on_click(cx.listener(|pane, _, _, cx| pane.refresh(cx))),
                ),
        )
        .child(
            uniform_list(
                "files",
                count,
                cx.processor(|pane, range: std::ops::Range<usize>, _window, cx| {
                    let pane = &*pane;
                    pane.tree
                        .rows()
                        .into_iter()
                        .enumerate()
                        .skip(range.start)
                        .take(range.len())
                        .map(|(ix, row)| pane.row(row, ix, cx))
                        .collect()
                }),
            )
            .track_scroll(&self.list)
            .flex_1()
            .px_1()
            .py_1(),
        )
    }
}

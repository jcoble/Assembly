//! `SearchPane` finds files and folders in the selected session's folder whose name holds what's typed
//! (`files/search`). Typing waits `DEBOUNCE`, then searches; the wait and the call are one task the pane holds, so a
//! newer search, a session switch or closing the pane drops the older one, which cancels its call on the backend.
//! "More" loads the next page the same way. It registers under the Search pane's name, after the placeholders. It is
//! drawn as the look's search: the shared field, the Names and Content segments, then each group of results under its
//! heading and count, in the shared file rows with the typed letters picked out.

use crate::core::theme::text::{TextRole, TextRoleExt as _};
use std::time::Duration;

use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::dock::{BasePanel, Panel, PanelEvent, panel_handle, register_panel};
use gpui_kit::component::input::{InputEvent, InputState};
use gpui_kit::component::tab::{Tab, TabBar};
use gpui_kit::component::{Icon, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use serde_json::json;

use super::entry::{ContentMatch, NameMatch, SearchResult};
use super::file_view::FileView;
use crate::components::file_row::file_row;
use crate::components::heading_label::heading_label;
use crate::components::match_text::match_text;
use crate::components::search_field::search_field;
use crate::core::intents::reveal_file_folder::RevealFileFolder;
use crate::core::socket::client::SocketClient;
use crate::core::socket::outcome::Outcome;
use crate::core::state::selected_session::SelectedSession;
use crate::core::theme::look::Look;
use crate::core::theme::metrics::{CORNER_PILL, FILE_ROW_GAP, GROUP_HEIGHT, SPACE};
use crate::core::ui::access::Control as _;
use crate::core::ui::access::control_ids::search as ids;
use crate::core::ui::file_icon::file_icon;
use crate::core::ui::pane_names::SEARCH;

/// How long typing waits before it searches.
pub const DEBOUNCE: Duration = Duration::from_millis(150);
/// Results per page.
const PAGE: u32 = 100;
/// A folder's mark, the size of a file's.
const MARK_SIZE: Pixels = px(14.);

pub struct SearchPane {
    focus: FocusHandle,
    input: Entity<InputState>,
    session: Option<String>,
    source: Option<crate::core::socket::row_source::ExpectedRowSource>,
    /// The query the results are for, trimmed.
    query: String,
    pub(super) matches: Vec<NameMatch>,
    /// The next page's cursor, while there is one.
    next: Option<String>,
    content: Vec<ContentMatch>,
    mode: &'static str,
    shown: bool,
    generation: u64,
    skipped: usize,
    truncated: bool,
    pub(super) error: Option<String>,
    selected: Option<String>,
    /// The search or page being loaded, with its wait; dropping it cancels its call.
    pub(super) searching: Option<Task<()>>,
    list: UniformListScrollHandle,
    _heard: Vec<Subscription>,
}

impl SearchPane {
    pub fn register(cx: &mut App) {
        register_panel(cx, SEARCH, |_, window, cx| {
            panel_handle(cx.new(|cx| SearchPane::new(window, cx)))
        });
    }

    pub fn new(window: &mut Window, cx: &mut Context<Self>) -> Self {
        let input = cx.new(|cx| InputState::new(window, cx).placeholder("Search files"));
        let heard = vec![
            cx.subscribe_in(&input, window, |pane, input, event, _, cx| {
                if let InputEvent::Change = event {
                    let query = input.read(cx).value().trim().to_string();
                    pane.search(query, DEBOUNCE, cx);
                }
            }),
            cx.observe(&SelectedSession::global(cx), |pane, _, cx| pane.follow(cx)),
        ];
        let mut pane = SearchPane {
            focus: cx.focus_handle(),
            input,
            session: None,
            source: None,
            query: String::new(),
            matches: Vec::new(),
            next: None,
            content: Vec::new(),
            mode: "name",
            shown: false,
            generation: 0,
            skipped: 0,
            truncated: false,
            error: None,
            selected: None,
            searching: None,
            list: UniformListScrollHandle::new(),
            _heard: heard,
        };
        pane.follow(cx);
        pane
    }

    /// Takes the selected session and searches it again for what's typed.
    fn follow(&mut self, cx: &mut Context<Self>) {
        let session = SelectedSession::global(cx)
            .read(cx)
            .session_id()
            .map(String::from);
        let source = SelectedSession::global(cx).read(cx).source().cloned();
        if session != self.session || source != self.source {
            self.session = session;
            self.source = source;
            self.search(self.query.clone(), Duration::ZERO, cx);
        }
    }

    /// Starts a new search for `query` after `wait`, dropping the one before it.
    pub(super) fn search(&mut self, query: String, wait: Duration, cx: &mut Context<Self>) {
        self.release_run(cx);
        self.generation = self.generation.wrapping_add(1);
        self.query = query;
        self.matches.clear();
        self.content.clear();
        self.skipped = 0;
        self.truncated = false;
        self.next = None;
        self.error = None;
        self.selected = None;
        self.searching = None;
        if self.shown && !self.query.is_empty() && self.session.is_some() {
            self.load(None, wait, cx);
        }
        cx.notify();
    }

    /// Loads the page at `cursor` after `wait`, adding it to the results.
    fn load(&mut self, cursor: Option<String>, wait: Duration, cx: &mut Context<Self>) {
        let (Some(session), Some(socket)) = (self.session.clone(), SocketClient::try_global(cx))
        else {
            self.error = Some("The backend isn't running.".into());
            return;
        };
        if !self.shown {
            return;
        }
        let generation = self.generation;
        let expected_owner = crate::core::state::workspace_panes::WorkspacePanes::key(cx)
            .filter(|key| {
                key.session_id == session
                    && FileView::workspace_source(key, cx).as_ref() == self.source.as_ref()
            })
            .map(|key| super::entry::FileOwnership {
                machine: key.machine.clone(),
                checkout_root: key.checkout_root.clone(),
                remote_session_id: key.session_id.clone(),
                file: None,
            });
        let args = json!({ "session": session, "query": self.query, "mode": self.mode, "searchId": cursor, "pageSize": PAGE, "expectedSource": self.source, "expectedOwner": expected_owner });
        self.searching = Some(cx.spawn(async move |pane, cx| {
            if !wait.is_zero() {
                cx.background_executor().timer(wait).await;
            }
            let Ok(call) = pane.update(cx, |_, cx| {
                socket
                    .read(cx)
                    .call::<SearchResult>("files", "search", args, None)
            }) else {
                return;
            };
            let outcome = call.await;
            let _ = pane.update(cx, |pane, cx| {
                if !pane.shown || pane.generation != generation {
                    return;
                }
                pane.searching = None;
                match outcome {
                    Outcome::Ok(page) => {
                        let mut paths: std::collections::HashSet<_> =
                            pane.matches.iter().map(|m| m.path.clone()).collect();
                        pane.matches.extend(
                            page.names
                                .into_iter()
                                .filter(|m| paths.insert(m.path.clone())),
                        );
                        pane.content.extend(page.content);
                        pane.next = page.more.then_some(page.search_id);
                        pane.skipped = page.skipped;
                        pane.truncated = page.truncated;
                    }
                    Outcome::Failed(failure) if failure.message.contains("cursor_changed") => {
                        pane.search(pane.query.clone(), Duration::ZERO, cx);
                        return;
                    }
                    Outcome::Failed(failure) => pane.error = Some(failure.message),
                    Outcome::Aborted => {
                        pane.error = Some("The connection dropped; type again to retry.".into())
                    }
                }
                cx.notify();
            });
        }));
    }

    fn release_run(&mut self, cx: &mut Context<Self>) {
        self.searching = None;
        if let (Some(search_id), Some(socket)) = (self.next.take(), SocketClient::try_global(cx)) {
            let call = socket.read(cx).call::<serde_json::Value>(
                "files",
                "releaseSearch",
                json!({ "searchId": search_id }),
                None,
            );
            call.ignore_reply();
        }
    }

    /// A line in a file holding what's typed, the look's "In conversations" row: the file's mark and the line's words
    /// with the typed letters picked out, then where it is in Meta text-3. A click opens the file at that place.
    fn content_row(&self, ix: usize, cx: &mut Context<Self>) -> AnyElement {
        let found = &self.content[ix];
        let look = Look::of(cx);
        let row_session = self.session.clone();
        let row_source = self.source.clone();
        let (path, line, column) = (found.path.clone(), found.line, found.column);
        file_row(("content", ix), false, cx)
            .h_auto()
            .py(SPACE[2])
            .child(
                h_flex()
                    .items_start()
                    .gap(FILE_ROW_GAP)
                    .min_w_0()
                    .child(file_icon(format!("content-mark:{ix}"), &found.path, cx).mt(SPACE[0]))
                    .child(
                        v_flex()
                            .min_w_0()
                            .child(div().truncate().text_color(look.text_2).child(match_text(
                                found.excerpt.trim().to_string(),
                                &self.query,
                                cx,
                            )))
                            .child(
                                div()
                                    .truncate()
                                    .text_role(TextRole::Meta, cx)
                                    .text_color(look.text_3)
                                    .child(format!("{}:{}:{}", found.path, line, column)),
                            ),
                    ),
            )
            .on_click(cx.listener(move |pane, _, window, cx| {
                if pane.session != row_session || pane.source != row_source {
                    return;
                }
                if let Some(session) = &pane.session {
                    FileView::open_at(
                        session.clone(),
                        path.clone(),
                        (Some(line), Some(column)),
                        true,
                        row_source.clone(),
                        window,
                        cx,
                    );
                }
            }))
            .into_any_element()
    }

    fn more(&mut self, cx: &mut Context<Self>) {
        if let (Some(cursor), None) = (self.next.clone(), &self.searching) {
            self.load(Some(cursor), Duration::ZERO, cx);
            cx.notify();
        }
    }

    /// A file or folder whose name holds what's typed, the shared file row: its mark, the name with the typed letters
    /// picked out, then its folder in Meta text-3, which ends in "…" when the column is narrow. A click opens the file,
    /// or shows a folder in the Files tab.
    fn row(&self, ix: usize, cx: &mut Context<Self>) -> AnyElement {
        let found = &self.matches[ix];
        let look = Look::of(cx);
        let row_session = self.session.clone();
        let row_source = self.source.clone();
        let picked = self.selected.as_deref() == Some(found.path.as_str());
        let path = found.path.clone();
        let folder_hit = found.is_directory;
        let folder = found
            .relative_path
            .rsplit_once('/')
            .map_or("", |(folder, _)| folder);
        let mark = if folder_hit {
            Icon::new(IconName::Folder)
                .size(MARK_SIZE)
                .text_color(look.text_3)
                .into_any_element()
        } else {
            file_icon(format!("mark:{ix}"), &found.path, cx).into_any_element()
        };
        file_row(ix, picked, cx)
            .child(
                h_flex()
                    .w_full()
                    .gap(FILE_ROW_GAP)
                    .min_w_0()
                    .overflow_hidden()
                    .whitespace_nowrap()
                    .child(mark)
                    .child(div().flex_none().text_color(look.text_2).child(match_text(
                        found.name.clone(),
                        &self.query,
                        cx,
                    )))
                    .child(
                        div()
                            .debug_selector(|| "search-folder".into())
                            .min_w_0()
                            .truncate()
                            .text_role(TextRole::Meta, cx)
                            .text_color(look.text_3)
                            .child(folder.to_string()),
                    ),
            )
            .on_click(cx.listener(move |pane, _, window, cx| {
                if pane.session != row_session || pane.source != row_source {
                    return;
                }
                pane.selected = Some(path.clone());
                if let Some(session) = pane.session.clone() {
                    if folder_hit {
                        window.dispatch_action(
                            Box::new(RevealFileFolder {
                                session_id: session,
                                path: path.clone(),
                                expected_source: row_source.clone(),
                            }),
                            cx,
                        );
                    } else {
                        FileView::open_at(
                            session,
                            path.clone(),
                            (None, None),
                            true,
                            row_source.clone(),
                            window,
                            cx,
                        );
                    }
                }
                cx.notify();
            }))
            .into_any_element()
    }
}

impl EventEmitter<PanelEvent> for SearchPane {}

impl Focusable for SearchPane {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus.clone()
    }
}

impl BasePanel for SearchPane {
    fn panel_name(&self) -> &'static str {
        SEARCH
    }

    fn set_active(&mut self, active: bool, _: &mut Window, cx: &mut Context<Self>) {
        if self.shown == active {
            return;
        }
        self.shown = active;
        if active {
            self.search(self.query.clone(), Duration::ZERO, cx);
        } else {
            self.generation = self.generation.wrapping_add(1);
            self.release_run(cx);
        }
    }

    /// The left column's tabs are permanent.
    fn closable(&self, _cx: &App) -> bool {
        false
    }
}

impl Panel for SearchPane {
    fn title(&mut self, _window: &mut Window, _cx: &mut Context<Self>) -> impl IntoElement {
        SEARCH
    }
}

impl SearchPane {
    /// The two ways to search, the kit's segmented bar with the look's outline on its track: by name, or by what a
    /// file holds. Picking one searches again.
    fn modes(&self, cx: &mut Context<Self>) -> TabBar {
        let mode = |label: &'static str, mode: &'static str| {
            Tab::new()
                .label(label)
                .on_click(cx.listener(move |pane, _, _, cx| {
                    pane.mode = mode;
                    pane.search(pane.query.clone(), Duration::ZERO, cx);
                }))
        };
        TabBar::new("search-modes")
            .segmented()
            .small()
            .flex_none()
            .border_1()
            .border_color(Look::of(cx).line)
            .selected_index(usize::from(self.mode != "name"))
            .child(mode("Names", "name"))
            .child(mode("Content", "content"))
    }

    /// A group of results under its heading and count, the ⌘K search's "Files 2"; a "+" says more pages wait.
    fn heading(&self, title: &'static str, count: usize, cx: &App) -> Div {
        let more = if self.next.is_some() { "+" } else { "" };
        h_flex()
            .h(GROUP_HEIGHT)
            .flex_none()
            .px(SPACE[4])
            .child(heading_label(title, Some(format!("{count}{more}")), cx))
    }
}

impl Render for SearchPane {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let modes = self.modes(cx);
        let look = Look::of(cx);
        // A status line in Meta: text-3, or the problem colour for an error.
        let line = |text: String| {
            div()
                .px(SPACE[4])
                .py(SPACE[2])
                .text_role(TextRole::Meta, cx)
                .text_color(look.text_3)
                .child(text)
        };
        let status = if self.session.is_none() {
            Some(line("Pick a session to search its files.".into()))
        } else if let Some(error) = &self.error {
            Some(line(error.clone()).text_color(look.problem))
        } else if self.searching.is_some() && self.matches.is_empty() && self.content.is_empty() {
            Some(line("Searching…".into()))
        } else if !self.query.is_empty() && self.matches.is_empty() && self.content.is_empty() {
            Some(line(format!(
                "No {} matches for \u{201c}{}\u{201d}.",
                self.mode, self.query
            )))
        } else {
            None
        };
        let (names, lines) = (self.matches.len(), self.content.len());
        v_flex()
            .track_focus(&self.focus)
            .size_full()
            // The top line, as in Sessions: the shared search field.
            .child(
                h_flex()
                    .pt(SPACE[5])
                    .px(SPACE[4])
                    .child(search_field(&self.input, cx).control(ids::FIELD, "Search files")),
            )
            // Then what to search, where Sessions has its filters.
            .child(h_flex().pt(SPACE[3]).px(SPACE[4]).child(modes))
            .when(self.skipped > 0 || self.truncated, |pane| {
                pane.child(line(format!(
                    "{} files skipped{}",
                    self.skipped,
                    if self.truncated {
                        "; scan reached its size limit"
                    } else {
                        ""
                    }
                )))
            })
            .children(status)
            .when(names > 0, |pane| {
                pane.child(self.heading("Files", names, cx)).child(
                    uniform_list(
                        "search-results",
                        names,
                        cx.processor(|pane, range: std::ops::Range<usize>, _window, cx| {
                            range.map(|ix| pane.row(ix, cx)).collect()
                        }),
                    )
                    .track_scroll(&self.list)
                    .flex_1()
                    .px(SPACE[3]),
                )
            })
            .when(lines > 0, |pane| {
                pane.child(self.heading("In files", lines, cx)).child(
                    uniform_list(
                        "content-results",
                        lines,
                        cx.processor(|pane, range: std::ops::Range<usize>, _, cx| {
                            range.map(|ix| pane.content_row(ix, cx)).collect()
                        }),
                    )
                    .track_scroll(&self.list)
                    .flex_1()
                    .px(SPACE[3]),
                )
            })
            // The next page, the kit's quiet button in text-2.
            .when(self.next.is_some(), |pane| {
                pane.child(
                    h_flex().px(SPACE[4]).py(SPACE[2]).child(
                        Button::new("search-more")
                            .control(ids::MORE, "Load more results")
                            .ghost()
                            .small()
                            .rounded(CORNER_PILL)
                            .text_color(look.text_2)
                            .label(if self.searching.is_some() {
                                "Loading…"
                            } else {
                                "More"
                            })
                            .on_click(cx.listener(|pane, _, _, cx| pane.more(cx))),
                    ),
                )
            })
    }
}

//! `SearchPane` finds files and folders in the selected session's folder whose name holds what's typed
//! (`files/search`). Typing waits `DEBOUNCE`, then searches; the wait and the call are one task the pane holds, so a
//! newer search, a session switch or closing the pane drops the older one, which cancels its call on the backend.
//! "More" loads the next page the same way. It registers under the Search pane's name, after the placeholders.

use std::time::Duration;

use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::dock::{BasePanel, Panel, PanelEvent, panel_handle, register_panel};
use gpui_kit::component::input::{Input, InputEvent, InputState};
use gpui_kit::component::{ActiveTheme as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use serde_json::json;

use super::entry::{ContentMatch, NameMatch, SearchResult};
use super::file_view::FileView;
use super::files_pane::{TREE_LINE, TREE_TEXT};
use crate::core::socket::client::SocketClient;
use crate::core::socket::outcome::Outcome;
use crate::core::state::selected_session::SelectedSession;
use crate::core::ui::pane_names::SEARCH;

/// How long typing waits before it searches.
pub const DEBOUNCE: Duration = Duration::from_millis(150);
/// Results per page.
const PAGE: u32 = 100;

pub struct SearchPane {
    focus: FocusHandle,
    input: Entity<InputState>,
    session: Option<String>,
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
        if session != self.session {
            self.session = session;
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
        let args = json!({ "session": session, "query": self.query, "mode": self.mode, "searchId": cursor, "pageSize": PAGE });
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

    fn content_row(&self, ix: usize, cx: &mut Context<Self>) -> AnyElement {
        let found = &self.content[ix];
        let (path, line, column) = (found.path.clone(), found.line, found.column);
        v_flex()
            .id(("content", ix))
            .px_2()
            .py_1()
            .cursor_pointer()
            .child(format!("{}:{}:{}", found.path, line, column))
            .child(
                div()
                    .text_sm()
                    .text_color(cx.theme().muted_foreground)
                    .child(found.excerpt.clone()),
            )
            .on_click(cx.listener(move |pane, _, window, cx| {
                if let Some(session) = &pane.session {
                    FileView::open_at(
                        session.clone(),
                        path.clone(),
                        Some(line),
                        Some(column),
                        true,
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

    fn row(&self, ix: usize, cx: &mut Context<Self>) -> AnyElement {
        let found = &self.matches[ix];
        let theme = cx.theme();
        let (muted, accent, secondary) = (theme.muted_foreground, theme.accent, theme.secondary);
        let picked = self.selected.as_deref() == Some(found.path.as_str());
        let path = found.path.clone();
        let folder_hit = found.is_directory;
        let folder = found
            .relative_path
            .rsplit_once('/')
            .map_or("", |(folder, _)| folder);
        h_flex()
            .id(ix)
            .w_full()
            .h(TREE_LINE)
            .px_2()
            .gap_2()
            .rounded(px(6.))
            .cursor_pointer()
            .whitespace_nowrap()
            .overflow_hidden()
            .when(picked, |row| row.bg(secondary))
            .when(!picked, |row| row.hover(|row| row.bg(accent)))
            .child(div().flex_none().text_size(TREE_TEXT).child(format!(
                "{}{}",
                found.name,
                if found.is_directory { "/" } else { "" }
            )))
            .child(
                div()
                    .debug_selector(|| "search-folder".into())
                    .min_w_0()
                    .overflow_hidden()
                    .text_ellipsis()
                    .text_sm()
                    .text_color(muted)
                    .child(folder.to_string()),
            )
            .on_click(cx.listener(move |pane, _, window, cx| {
                pane.selected = Some(path.clone());
                if let (false, Some(session)) = (folder_hit, pane.session.clone()) {
                    FileView::open(session, path.clone(), window, cx);
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

impl Render for SearchPane {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let theme = cx.theme();
        let (muted, danger) = (theme.muted_foreground, theme.danger);
        let line = |text: String| div().px_3().py_1().text_sm().child(text);
        let status = if self.session.is_none() {
            Some(line("Pick a session to search its files.".into()).text_color(muted))
        } else if let Some(error) = &self.error {
            Some(line(error.clone()).text_color(danger))
        } else if self.searching.is_some() && self.matches.is_empty() && self.content.is_empty() {
            Some(line("Searching…".into()).text_color(muted))
        } else if !self.query.is_empty() && self.matches.is_empty() && self.content.is_empty() {
            Some(
                line(format!(
                    "No {} matches for \u{201c}{}\u{201d}.",
                    self.mode, self.query
                ))
                .text_color(muted),
            )
        } else {
            None
        };
        v_flex()
            .track_focus(&self.focus)
            .size_full()
            .child(
                h_flex()
                    .px_2()
                    .py_1()
                    .gap_1()
                    .child(Button::new("search-names").ghost().label("Names").on_click(
                        cx.listener(|pane, _, _, cx| {
                            pane.mode = "name";
                            pane.search(pane.query.clone(), Duration::ZERO, cx);
                        }),
                    ))
                    .child(
                        Button::new("search-content")
                            .ghost()
                            .label("Content")
                            .on_click(cx.listener(|pane, _, _, cx| {
                                pane.mode = "content";
                                pane.search(pane.query.clone(), Duration::ZERO, cx);
                            })),
                    ),
            )
            .child(div().px_2().py_2().child(Input::new(&self.input)))
            .when(self.skipped > 0 || self.truncated, |pane| {
                pane.child(
                    line(format!(
                        "{} files skipped{}",
                        self.skipped,
                        if self.truncated {
                            "; scan reached its size limit"
                        } else {
                            ""
                        }
                    ))
                    .text_color(muted),
                )
            })
            .children(status)
            .when(!self.matches.is_empty(), |pane| {
                pane.child(
                    uniform_list(
                        "search-results",
                        self.matches.len(),
                        cx.processor(|pane, range: std::ops::Range<usize>, _window, cx| {
                            range.map(|ix| pane.row(ix, cx)).collect()
                        }),
                    )
                    .track_scroll(&self.list)
                    .flex_1()
                    .px_1(),
                )
            })
            .when(!self.content.is_empty(), |pane| {
                pane.child(
                    uniform_list(
                        "content-results",
                        self.content.len(),
                        cx.processor(|pane, range: std::ops::Range<usize>, _, cx| {
                            range.map(|ix| pane.content_row(ix, cx)).collect()
                        }),
                    )
                    .track_scroll(&self.list)
                    .flex_1(),
                )
            })
            .when(self.next.is_some(), |pane| {
                pane.child(
                    h_flex().px_2().py_1().child(
                        Button::new("search-more")
                            .ghost()
                            .compact()
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

//! The Tasks tab's list: reading pages of the saved tasks, following what's pushed, and drawing it.

use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::input::Input;
use gpui_kit::component::{ActiveTheme as _, Disableable as _, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use std::rc::Rc;

use crate::core::state::selected_session::SelectedSession;
use crate::core::state::sessions::Sessions;
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::core::ui::filter_pills::{FilterGroup, FilterOption};
use crate::features::tasks::task_export::*;
use crate::features::tasks::tasks_state::*;
use crate::features::tasks::tasks_view::{OnViewChange, ViewChange, task_row, view_menu};

use super::{ROW_HEIGHT, TasksPane};

impl TasksPane {
    pub(super) fn settings_pushed(&mut self, view: NotionSettingsView, cx: &mut Context<Self>) {
        let first = self.state.settings.is_none();
        let changed = self.state.set_settings(view);
        if changed {
            self.detail = None;
            self._detail_heard = None;
            self.first_refresh_done = first && self.first_refresh_done;
            self.list.reset(0);
        }
        let connected = self.state.connected();
        self.setup.update(cx, |setup, cx| setup.sync(connected, cx));
        if connected && (changed || first) {
            self.read_first(cx);
        }
        cx.notify();
    }

    pub(super) fn tasks_pushed(&mut self, cx: &mut Context<Self>) {
        if !self.state.connected() {
            return;
        }
        let (token, query) = self.state.same_window();
        self.read(token, query, true, cx);
    }

    pub(super) fn read_first(&mut self, cx: &mut Context<Self>) {
        let (token, query) = self.state.first_page();
        self.read(token, query, false, cx);
    }

    pub(super) fn read(
        &mut self,
        token: ReadToken,
        query: NotionQuery,
        keep_place: bool,
        cx: &mut Context<Self>,
    ) {
        let call = self.client.list(query, cx);
        self.reading = Some(cx.spawn(async move |pane, cx| {
            let result = call.await;
            let _ = pane.update(cx, |pane, cx| {
                pane.page_arrived(token, result, keep_place, cx)
            });
        }));
        cx.notify();
    }

    pub(super) fn page_arrived(
        &mut self,
        token: ReadToken,
        result: Result<NotionTaskPage, NotionError>,
        keep_place: bool,
        cx: &mut Context<Self>,
    ) {
        match self.state.page_result(token, result) {
            PageOutcome::Stale => return,
            PageOutcome::Failed => {
                self.setup_follows(cx);
            }
            PageOutcome::FiltersDropped => {
                self.sync_pills(cx);
                self.query_changed(cx);
                return;
            }
            PageOutcome::Appended { from, count } => self.list.splice(from..from, count),
            PageOutcome::Replaced => {
                let place = self.list.logical_scroll_top();
                self.list.reset(self.state.rows.len());
                if keep_place {
                    self.list.scroll_to(place);
                }
                self.sync_pills(cx);
                if self.state.refollow() {
                    self.query_changed(cx);
                    return;
                }
                self.refresh_if_new(cx);
            }
        }
        cx.notify();
    }

    /// A connection with nothing saved yet has never asked Notion, so it asks once.
    pub(super) fn refresh_if_new(&mut self, cx: &mut Context<Self>) {
        if self.state.fetched_ms == 0 && !self.first_refresh_done {
            self.first_refresh_done = true;
            self.refresh(cx);
        }
    }

    pub(super) fn setup_follows(&mut self, cx: &mut Context<Self>) {
        let connected = !self.state.needs_connecting();
        self.setup.update(cx, |setup, cx| setup.sync(connected, cx));
    }

    pub(super) fn refresh(&mut self, cx: &mut Context<Self>) {
        let Some(generation) = self.state.settings.as_ref().map(|view| view.generation) else {
            return;
        };
        self.refreshing = true;
        let call = self.client.refresh(generation, cx);
        self.refresh_call = Some(cx.spawn(async move |pane, cx| {
            let result = call.await;
            let _ = pane.update(cx, |pane, cx| {
                pane.refreshing = false;
                if let Err(error) = result {
                    pane.state.note_error(&error);
                    pane.setup_follows(cx);
                }
                cx.notify();
            });
        }));
        cx.notify();
    }

    /// The query changed by the person's hand: first page, top of the list, saved.
    pub(super) fn query_changed(&mut self, cx: &mut Context<Self>) {
        let mut saved = self.state.query.clone();
        saved.offset = 0;
        saved.limit = PAGE_SIZE;
        let call = self.client.save_view(saved, cx);
        self.saving_view = Some(cx.spawn(async move |_, _| {
            let _ = call.await;
        }));
        self.read_first(cx);
    }

    pub(super) fn maybe_more(&mut self, visible_end: usize, cx: &mut Context<Self>) {
        if visible_end + OVERSCAN >= self.state.rows.len()
            && let Some((token, query)) = self.state.next_page()
        {
            self.read(token, query, true, cx);
        }
    }

    /// The pills offer what the snapshot holds, and show the query's picks.
    pub(super) fn sync_pills(&mut self, cx: &mut Context<Self>) {
        let group = |label: &str, values: &[String], picked: &[String]| {
            let options = values
                .iter()
                .map(|value| FilterOption {
                    value: value.clone(),
                    label: value.clone(),
                })
                .collect();
            let mut group = FilterGroup::new(label, options);
            group.picked = picked.to_vec();
            group
        };
        let groups = vec![
            group("Status", &self.state.statuses, &self.state.query.statuses),
            group(
                "Priority",
                &self.state.priorities,
                &self.state.query.priorities,
            ),
        ];
        self.pills.update(cx, |pills, cx| {
            pills.state.groups = groups;
            cx.notify();
        });
    }

    pub(super) fn follow_selected(&mut self, cx: &mut Context<Self>) {
        let root = SelectedSession::try_global(cx).and_then(|selected| {
            let id = selected.read(cx).session_id()?.to_owned();
            let sessions = Sessions::try_global(cx)?;
            let sessions = sessions.read(cx);
            let session = sessions.items().iter().find(|item| item.session_id == id)?;
            session
                .checkout_root
                .clone()
                .or_else(|| session.root.clone())
        });
        if self.state.follow_root(root.as_deref()) {
            self.query_changed(cx);
        }
        cx.notify();
    }

    pub(super) fn view_changed(&mut self, change: ViewChange, cx: &mut Context<Self>) {
        match change {
            ViewChange::Sort(sort) => self.state.query.sort = sort.into(),
            ViewChange::Direction(direction) => self.state.query.direction = direction.into(),
            ViewChange::Project(project) => self.state.query.project_id = project,
        }
        self.query_changed(cx);
    }

    pub(super) fn header(&mut self, cx: &mut Context<Self>) -> impl IntoElement {
        let entity = cx.entity();
        let on_change: OnViewChange = Rc::new(move |change, _, cx| {
            entity.update(cx, |pane, cx| pane.view_changed(change, cx));
        });
        v_flex()
            .flex_none()
            .gap_2()
            .px_2()
            .pt_1p5()
            .pb_2()
            .child(
                h_flex()
                    .gap_1()
                    .items_center()
                    .child(div().flex_1().min_w_0().child(view_menu(
                        &self.state.query,
                        &self.state.projects,
                        on_change,
                    )))
                    .child(
                        Button::new("tasks-settings")
                            .ghost()
                            .small()
                            .icon(IconName::Settings)
                            .tooltip("Task settings")
                            .on_click(cx.listener(|pane, _, _, cx| {
                                pane.state.setup_requested = !pane.state.setup_requested;
                                cx.notify();
                            })),
                    )
                    .child(
                        Button::new("tasks-refresh")
                            .ghost()
                            .small()
                            .icon(IconName::RefreshCw)
                            .tooltip("Refresh tasks")
                            .disabled(self.refreshing || !self.state.connected())
                            .on_click(cx.listener(|pane, _, _, cx| pane.refresh(cx))),
                    ),
            )
            .child(
                Input::new(&self.search)
                    .small()
                    .prefix(gpui_kit::component::Icon::new(IconName::Search))
                    .suffix(
                        div()
                            .text_role(TextRole::Meta, cx)
                            .text_color(cx.theme().muted_foreground)
                            .child(self.state.rows.len().to_string()),
                    ),
            )
    }

    pub(super) fn filtered(&self) -> bool {
        let query = &self.state.query;
        query.search.is_some()
            || query.project_id.is_some()
            || !query.statuses.is_empty()
            || !query.priorities.is_empty()
    }

    pub(super) fn empty_list(&self, cx: &App) -> impl IntoElement {
        let (title, body) = if self.state.loading || self.refreshing {
            ("Loading tasks…", "Reading the last saved list.")
        } else if self.state.needs_connecting() {
            (
                "Connect Notion to see tasks",
                "Your connection stays on this Mac.",
            )
        } else if self.filtered() {
            ("No matching tasks", "Try a different search or filter.")
        } else {
            (
                "No tasks saved yet",
                "Refresh to ask Notion for the latest list.",
            )
        };
        v_flex()
            .flex_1()
            .items_center()
            .justify_center()
            .gap_1()
            .p_4()
            .child(div().font_weight(FontWeight::MEDIUM).child(title))
            .child(div().text_color(cx.theme().muted_foreground).child(body))
    }

    pub(super) fn draw_list(&mut self, cx: &mut Context<Self>) -> impl IntoElement {
        let error = self.state.error.clone();
        let danger = cx.theme().danger;
        let muted = cx.theme().muted_foreground;
        let notes = v_flex()
            .flex_none()
            .gap_1()
            .px_2()
            .when_some(error, |notes, error| {
                notes.child(div().pb_1().text_color(danger).child(error))
            })
            .when(self.state.no_session_project(), |notes| {
                notes.child(
                    div()
                        .pb_1()
                        .text_role(TextRole::Meta, cx)
                        .text_color(muted)
                        .child("No Notion project matches this session. Showing all projects."),
                )
            })
            .when(self.state.truncated, |notes| {
                notes.child(
                    div()
                        .pb_1()
                        .text_role(TextRole::Meta, cx)
                        .text_color(muted)
                        .child("Showing the newest 500 tasks. Notion has more."),
                )
            });
        // Rows too few to scroll never send a scroll event, so the drawn height asks for the next page.
        let filled = self.state.rows.len() as f32 * ROW_HEIGHT;
        if self.list.viewport_bounds().size.height > px(filled) {
            self.maybe_more(self.state.rows.len(), cx);
        }
        let header = self.header(cx);
        v_flex()
            .size_full()
            .min_h_0()
            .child(header)
            .when(
                !self.state.statuses.is_empty() || !self.state.priorities.is_empty(),
                |column| column.child(div().flex_none().px_2().pb_2().child(self.pills.clone())),
            )
            .when(self.state.show_setup(), |column| {
                column.child(div().flex_none().px_2().pb_2().child(self.setup.clone()))
            })
            .child(notes)
            .map(|column| {
                if self.state.rows.is_empty() {
                    column.child(self.empty_list(cx))
                } else {
                    let filtered = self.state.query.project_id.is_some();
                    column.child(
                        list(
                            self.list.clone(),
                            cx.processor(move |pane, index: usize, _, cx| {
                                let Some(row) = pane.state.rows.get(index) else {
                                    return Empty.into_any_element();
                                };
                                let open = cx.listener(move |pane, _: &ClickEvent, window, cx| {
                                    pane.open_row(index, window, cx);
                                });
                                div()
                                    .px_2()
                                    .child(task_row(index, row, filtered, open, cx))
                                    .into_any_element()
                            }),
                        )
                        .flex_1()
                        .min_h_0(),
                    )
                }
            })
    }
}

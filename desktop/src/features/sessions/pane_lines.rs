//! The sessions pane's list: its lines follow the rail's answer (only the lines that changed are re-measured, so the
//! list keeps its place), each line draws as a project's header or a session row. The next page loads near the end (the page before the held ones
//! while their start is).

use std::rc::Rc;

use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::list_entries::{changed_lines, list_entries, same_line};
use super::list_entry::ListEntry;
use super::project_header::{ProjectHeader, SectionHeader};
use super::session_row::{OnMark, OnSession, RowContext, SessionRow};
use super::sessions_pane::SessionsPane;
use crate::core::state::machines::Machines;
use crate::core::state::selected_session::SelectedSession;

/// Load the next page when the line this far from the end is in view.
const LOAD_OLDER_WITHIN: usize = 20;

impl SessionsPane {
    /// Lays the rail out again, telling the list which lines changed, and, once this frame is drawn, loads the next
    /// page if the end is in view.
    pub(super) fn follow_entries(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let entries = list_entries(&self.rail);
        let keys = entries
            .iter()
            .filter_map(|entry| match entry {
                ListEntry::Session(row) => Some(super::drop_down::rail_details_key(row)),
                _ => None,
            })
            .collect::<std::collections::HashSet<_>>();
        self.drops.update(cx, |drops, cx| {
            drops.retain_keys(|key| keys.contains(key), cx)
        });
        if entries != *self.entries {
            let (old, count) = changed_lines(&self.entries, &entries);
            // A splice moves a place inside the changed lines to their start, so the line at the top stays in view
            // by its identity instead. Unscrolled at the rail's real start, the list stays there and shows what came
            // in; at the start of a window further down, it keeps its line.
            let top = self.lines.logical_scroll_top();
            let kept = (top != ListOffset::default() || !self.from_start)
                .then(|| same_line(&self.entries, &entries, &old, top.item_ix))
                .flatten();
            self.lines.splice(old, count);
            if let Some(item_ix) = kept {
                self.lines.scroll_to(ListOffset { item_ix, ..top });
            } else if self.from_start && top == ListOffset::default() {
                self.lines.scroll_to(ListOffset::default());
            }
            self.entries = Rc::new(entries);
            self.from_start = self.rail.ahead == 0;
        }
        cx.on_next_frame(window, |pane, _, cx| pane.page_if_near_end(cx));
    }

    /// Loads the next page while the end is in view, and the rows before the held ones while their start is,
    /// unless the last read failed (then only Retry asks again).
    fn page_if_near_end(&mut self, cx: &mut Context<Self>) {
        let top = self.lines.logical_scroll_top().item_ix;
        let view = self.entries[top.min(self.entries.len())..]
            .iter()
            .find_map(|line| match line {
                ListEntry::Session(entry) => Some(entry.session_id.clone()),
                _ => None,
            });
        self.rail.look_at(view);
        if self.rail.error.is_some() {
            return;
        }
        let near = self.entries.len().saturating_sub(LOAD_OLDER_WITHIN);
        let mut read = None;
        if self.lines.item_is_below_viewport(near) == Some(false) {
            read = self.rail.more();
        }
        // A short list shows its end and its start at once: with no next page, the rows (and headings) above load.
        if read.is_none() && top < LOAD_OLDER_WITHIN {
            read = self.rail.earlier();
        }
        self.send(read, cx);
    }

    /// Draws line `at` of the list.
    pub(super) fn line_renderer(
        &self,
        cx: &mut Context<Self>,
    ) -> impl FnMut(usize, &mut Window, &mut App) -> AnyElement + 'static {
        let entries = self.entries.clone();
        let now = self.now;
        let drops = self.drops.clone();
        let running_drawn = self.running_drawn.clone();
        let pane = cx.entity().downgrade();
        let on_mark: OnMark = {
            let pane = pane.clone();
            Rc::new(move |session_id, to, cx| {
                pane.update(cx, |pane, cx| pane.mark(session_id, to, cx))
                    .ok();
            })
        };
        let on_open: OnSession = {
            let pane = pane.clone();
            Rc::new(move |session_id, route, window, cx| {
                pane.update(cx, |pane, cx| pane.open(session_id, route, window, cx))
                    .ok();
            })
        };
        move |at, _, cx| {
            let Some(entry) = entries.get(at).cloned() else {
                return Empty.into_any_element();
            };
            let row = |entry: &super::rail_page::RailEntry, cx: &mut App| RowContext {
                selected: entry.source.as_ref().is_some_and(|source| {
                    SelectedSession::global(cx)
                        .read(cx)
                        .key()
                        .is_some_and(|key| {
                            key.machine == source.machine && key.id == entry.session_id
                        })
                }),
                needs_you: super::sessions_pane::engine_rows::needs_you(entry, cx),
                offline: Machines::offline(entry.machine.as_deref(), cx),
                now,
                arrow: None,
                on_open: on_open.clone(),
                on_mark: on_mark.clone(),
            };
            match entry {
                ListEntry::Section {
                    key: header,
                    fold_key,
                    label,
                    count,
                    folded,
                } => {
                    let (pane, key) = (pane.clone(), fold_key.clone());
                    SectionHeader::new(fold_key, label, count, folded, move |_, _, cx| {
                        pane.update(cx, |pane, cx| {
                            let read = pane.rail.toggle_fold(key.clone(), header.clone());
                            pane.send(Some(read), cx);
                        })
                        .ok();
                    })
                    .into_any_element()
                }
                ListEntry::Project {
                    key: header,
                    fold_key,
                    label,
                    repo_url: _,
                    machines,
                    count,
                    folded,
                } => {
                    let (toggle_pane, key) = (pane.clone(), fold_key.clone());
                    let new_pane = pane.clone();
                    ProjectHeader::new(
                        label,
                        header.clone(),
                        machines,
                        count,
                        folded,
                        move |_, _, cx| {
                            toggle_pane
                                .update(cx, |pane, cx| {
                                    let read = pane.rail.toggle_fold(key.clone(), header.clone());
                                    pane.send(Some(read), cx);
                                })
                                .ok();
                        },
                        move |_, window, cx| {
                            new_pane
                                .update(cx, |pane, cx| pane.show_draft(window, cx))
                                .ok();
                        },
                    )
                    .into_any_element()
                }
                ListEntry::Session(entry) => {
                    if entry.running {
                        running_drawn.set(true);
                    }
                    let mut context = row(&entry, cx);
                    let key = super::drop_down::rail_details_key(&entry);
                    let open = drops.read(cx).is_open(&key);
                    let toggle_key = key.clone();
                    let toggles = drops.clone();
                    let native_route = entry.machine.clone();
                    let parent =
                        entry
                            .source
                            .as_ref()
                            .map(|source| crate::core::engine::SessionKey {
                                machine: source.machine.clone(),
                                id: entry.session_id.clone(),
                            });
                    let toggle_details: Rc<dyn Fn(&mut App)> = Rc::new(move |cx| {
                        let Some(parent) = parent.clone() else {
                            return;
                        };
                        if toggles.read(cx).is_open(&toggle_key) {
                            toggles.update(cx, |drops, cx| drops.shut(cx));
                            return;
                        }
                        let details = cx.new(|cx| {
                            super::drop_down::rail_details::RailDetails::new(
                                parent,
                                native_route.clone(),
                                cx,
                            )
                        });
                        toggles.update(cx, |drops, cx| {
                            drops.toggle(
                                toggle_key.clone(),
                                move |_, _| details.clone().into_any_element(),
                                cx,
                            )
                        });
                    });
                    // An open row keeps its arrow even once its last question is answered, so it can shut.
                    let openable =
                        open || super::sessions_pane::engine_rows::has_details(&entry, cx);
                    if openable {
                        context.arrow = Some(crate::core::ui::three_line_row::RowArrow {
                            open,
                            on_toggle: toggle_details.clone(),
                        });
                    }
                    let keys = drops.clone();
                    let key_for_header = key.clone();
                    let showing = drops.read(cx).is_showing(&key);
                    // An open row and its body are one fill, the one a selected row has. The kit's item would lay its own over it.
                    context.selected = context.selected && !showing;
                    let row_height = crate::core::ui::three_line_row::row_height(cx);
                    let growing = crate::core::ui::row_drop_down::GrowingBody::new(
                        key.clone(),
                        drops.clone(),
                        row_height,
                    );
                    let band = crate::core::ui::three_line_row::selected_fill(cx);
                    let radius = gpui_kit::component::ActiveTheme::theme(cx).radius;
                    gpui_kit::component::v_flex()
                        .id(SharedString::from(format!("rail-header:{key}")))
                        .w_full()
                        .rounded(radius)
                        .overflow_hidden()
                        .when(showing, |row| row.bg(band))
                        .on_key_down(move |event, _, cx| {
                            let code = event.keystroke.key.as_str();
                            if code == "right" {
                                if openable && !keys.read(cx).is_open(&key_for_header) {
                                    toggle_details(cx);
                                }
                                cx.stop_propagation();
                            } else if matches!(code, "left" | "escape") {
                                keys.update(cx, |drops, cx| {
                                    drops.handle_key(
                                        key_for_header.clone(),
                                        code,
                                        |_, _| Empty.into_any_element(),
                                        cx,
                                    );
                                });
                                cx.stop_propagation();
                            }
                        })
                        .child(SessionRow::new(*entry, context))
                        .child(growing)
                        .map(|row| div().w_full().px_2().py_px().child(row))
                        .into_any_element()
                }
            }
        }
    }
}

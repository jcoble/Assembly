//! The sessions pane's list: its lines follow the rail's answer (only the lines that changed are re-measured, so the
//! list keeps its place), each line draws as a project's header or a session row. The next page loads near the end (the page before the held ones
//! while their start is).

use std::rc::Rc;

use gpui_kit::*;

use super::list_entries::{changed_lines, list_entries, same_line};
use super::list_entry::ListEntry;
use super::project_header::ProjectHeader;
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
        if entries != *self.entries {
            let (old, count) = changed_lines(&self.entries, &entries);
            // A splice moves a place inside the changed lines to their start, so the line at the top stays in view
            // by its identity instead. Unscrolled at the tab's real start, the list stays there and shows what came
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
        if self.lines.item_is_below_viewport(near) == Some(false) {
            let read = self.rail.more();
            self.send(read, cx);
        } else if top < LOAD_OLDER_WITHIN {
            let read = self.rail.earlier();
            self.send(read, cx);
        }
    }

    /// Draws line `at` of the list.
    pub(super) fn line_renderer(
        &self,
        cx: &mut Context<Self>,
    ) -> impl FnMut(usize, &mut Window, &mut App) -> AnyElement + 'static {
        let entries = self.entries.clone();
        let now = self.now;
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
            Rc::new(move |session_id, window, cx| {
                pane.update(cx, |pane, cx| pane.open(session_id, window, cx))
                    .ok();
            })
        };
        move |at, _, cx| {
            let Some(entry) = entries.get(at).cloned() else {
                return Empty.into_any_element();
            };
            let row = |entry: &super::rail_page::RailEntry, cx: &App| RowContext {
                selected: SelectedSession::global(cx).read(cx).session_id()
                    == Some(&entry.session_id),
                offline: Machines::offline(entry.machine.as_deref(), cx),
                now,
                on_open: on_open.clone(),
                on_mark: on_mark.clone(),
            };
            match entry {
                ListEntry::Project {
                    label,
                    repo_url,
                    machine,
                } => {
                    let (pane, at) = (pane.clone(), repo_url.clone());
                    ProjectHeader::new(label, repo_url, move |_, window, cx| {
                        pane.update(cx, |pane, cx| pane.new_in(&at, machine.clone(), window, cx))
                            .ok();
                    })
                    .into_any_element()
                }
                ListEntry::Session(entry) => {
                    if entry.running {
                        running_drawn.set(true);
                    }
                    let context = row(&entry, cx);
                    SessionRow::new(*entry, context).into_any_element()
                }
            }
        }
    }
}

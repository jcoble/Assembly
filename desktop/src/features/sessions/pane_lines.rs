//! The sessions pane's list: its lines follow the grouped sessions (only the lines that changed are re-measured, so
//! the list keeps its place), each line draws as a group's header, a project's header or a session's row, and older
//! pages load while the list's end is in view.

use std::rc::Rc;

use gpui_kit::*;

use super::group_header::GroupHeader;
use super::list_entries::{changed_lines, list_entries, same_line};
use super::list_entry::ListEntry;
use super::project_header::ProjectHeader;
use super::session_row::{OnMark, SessionRow};
use super::sessions_pane::SessionsPane;
use super::status::Status;
use crate::core::state::machines::Machines;
use crate::core::state::selected_session::SelectedSession;

/// Load the next older page when the line this far from the end is in view.
const LOAD_OLDER_WITHIN: usize = 20;

impl SessionsPane {
    /// Lays the sessions out again, telling the list which lines changed, and, once this frame is drawn, loads the
    /// next older page if the end is in view.
    pub(super) fn follow_entries(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let entries = self.sessions.as_ref().map_or_else(Vec::new, |sessions| {
            let sessions = sessions.read(cx);
            list_entries(sessions.items(), self.settled_open, sessions.loaded())
        });
        if entries != *self.entries {
            let (old, count) = changed_lines(&self.entries, &entries);
            // A splice moves a place inside the changed lines to their start, so the line at the top stays in view
            // by its identity instead. Unscrolled, the list stays at its top and shows what came in.
            let top = self.lines.logical_scroll_top();
            let kept = (top != ListOffset::default())
                .then(|| same_line(&self.entries, &entries, &old, top.item_ix))
                .flatten();
            self.lines.splice(old, count);
            if let Some(item_ix) = kept {
                self.lines.scroll_to(ListOffset { item_ix, ..top });
            }
            self.entries = Rc::new(entries);
        }
        cx.on_next_frame(window, |pane, _, cx| pane.page_if_near_end(cx));
    }

    /// Loads the next page while the end is in view (the open sessions', then Settled's while it's open), unless the
    /// last one failed (then only Retry asks again).
    fn page_if_near_end(&mut self, cx: &mut Context<Self>) {
        let Some(sessions) = self.sessions.clone() else {
            return;
        };
        if sessions.read(cx).older_error().is_some() {
            return;
        }
        let near = self.entries.len().saturating_sub(LOAD_OLDER_WITHIN);
        if self.lines.item_is_below_viewport(near) == Some(false) {
            let settled_open = self.settled_open;
            sessions.update(cx, |sessions, cx| sessions.load_next(settled_open, cx));
        }
    }

    /// Draws line `at` of the list.
    pub(super) fn line_renderer(
        &self,
        cx: &mut Context<Self>,
    ) -> impl FnMut(usize, &mut Window, &mut App) -> AnyElement + 'static {
        let entries = self.entries.clone();
        let sessions = self.sessions.clone();
        let pane = cx.entity().downgrade();
        let on_mark: OnMark = {
            let pane = pane.clone();
            Rc::new(move |session_id, to, cx| {
                pane.update(cx, |pane, cx| pane.mark(session_id, to, cx))
                    .ok();
            })
        };
        move |at, _, cx| {
            let Some(entry) = entries.get(at).cloned() else {
                return Empty.into_any_element();
            };
            match entry {
                ListEntry::Group { status, open } => {
                    let header = GroupHeader::new(status, open);
                    if status != Status::Settled {
                        return header.into_any_element();
                    }
                    let pane = pane.clone();
                    header
                        .on_toggle(move |_, _, cx| {
                            pane.update(cx, |pane, cx| {
                                pane.settled_open = !pane.settled_open;
                                cx.notify();
                            })
                            .ok();
                        })
                        .into_any_element()
                }
                ListEntry::Project {
                    status,
                    folder,
                    machine,
                } => {
                    let (pane, at, on) = (pane.clone(), folder.clone(), machine.clone());
                    ProjectHeader::new(status, folder, machine, move |_, window, cx| {
                        pane.update(cx, |pane, cx| pane.new_in(&at, on.clone(), window, cx))
                            .ok();
                    })
                    .into_any_element()
                }
                ListEntry::Session(id) => {
                    let selected = SelectedSession::global(cx).read(cx).session_id() == Some(&id);
                    let item = sessions.as_ref().and_then(|sessions| {
                        let items = sessions.read(cx).items();
                        items.iter().find(|item| item.session_id == id).cloned()
                    });
                    match item {
                        Some(item) => {
                            let offline = Machines::offline(item.machine.as_deref(), cx);
                            SessionRow::of(&item, selected, offline)
                                .on_mark(on_mark.clone())
                                .into_any_element()
                        }
                        None => Empty.into_any_element(),
                    }
                }
            }
        }
    }
}

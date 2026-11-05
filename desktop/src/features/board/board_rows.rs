//! How the panel draws each listed row.
use gpui_kit::assets::IconName;
use gpui_kit::component::{ActiveTheme as _, Icon, Sizable as _, h_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::BoardPanel;
use crate::activity::{RunRow, StatusChip};
use crate::core::engine::workflow_items::RunItem;
use crate::core::engine::{EngineKey, SessionKey};
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::core::ui::activity::run_view::now_ms;
use crate::features::board::board_queries::is_terminal;
use crate::features::board::board_sections::{
    Entry, group_heading, machine_label, offline_reason, offline_since,
};
use crate::features::board::question_row::{card_key, need_card};
use crate::features::board::run_steps::pill;
use crate::features::board::session_tabs::{session_entry, task_label};

fn row_machine(entry: &Entry) -> Option<&str> {
    match entry {
        Entry::Need(key) | Entry::Run(key) | Entry::Session(key) => Some(&key.machine),
        _ => None,
    }
}

impl BoardPanel {
    fn run_row(&self, run: &RunItem, this: WeakEntity<Self>) -> RunRow {
        let steps = self.data.steps_of(run);
        let open_need = steps.iter().any(|step| {
            step.needs_me_ids.iter().any(|id| {
                let key = EngineKey {
                    machine: run.machine.clone(),
                    kind: "needs_me".into(),
                    id: id.clone(),
                };
                self.data
                    .needs
                    .get(&key)
                    .is_some_and(|need| need.state == "open")
            })
        });
        let state = pill(run, &steps, open_need);
        let running = steps
            .iter()
            .find(|step| step.state == "running")
            .map(|step| step.label_present.clone());
        let line = run
            .display_prerequisite
            .clone()
            .or(running)
            .unwrap_or_else(|| state.label().to_owned());
        RunRow {
            key: card_key(&run.key()),
            title: run.title.clone().unwrap_or_else(|| run.id.clone()).into(),
            task_key: run.task_url.as_deref().map(task_label),
            step_line: line.into(),
            done: steps
                .iter()
                .filter(|step| step.state == "succeeded")
                .count(),
            total: steps.len(),
            tone: state.tone(),
            pill: Some(StatusChip::new(state.label(), state.tone()).into_any_element()),
            body: Self::run_body(this, run.key()),
            drops: self.run_drops.clone(),
        }
    }

    pub(super) fn row(
        &mut self,
        index: usize,
        _: &mut Window,
        cx: &mut Context<Self>,
    ) -> AnyElement {
        #[cfg(test)]
        {
            self.built_rows += 1;
        }
        let Some(entry) = self.entries.get(index).cloned() else {
            return Empty.into_any_element();
        };
        let muted = cx.theme().muted_foreground;
        let now = now_ms();
        let row = match entry {
            Entry::Nothing(words) => div()
                .py_4()
                .text_color(muted)
                .child(words)
                .into_any_element(),
            Entry::Fold { id, label, folded } => h_flex()
                .id(id)
                .gap_1()
                .py_1()
                .text_color(muted)
                .cursor_pointer()
                .child(
                    Icon::new(if folded {
                        IconName::ChevronRight
                    } else {
                        IconName::ChevronDown
                    })
                    .xsmall(),
                )
                .child(label)
                .on_click(cx.listener(move |panel, _, _, cx| panel.toggle_fold(id, cx)))
                .into_any_element(),
            Entry::Group { machine } => {
                let feed = self.feed.read(cx);
                let line = group_heading(
                    &machine_label(&machine, feed, cx),
                    offline_reason(&machine, feed, cx).is_some(),
                    offline_since(&machine, feed, cx),
                );
                div()
                    .pt_2()
                    .text_role(TextRole::Meta, cx)
                    .text_color(muted)
                    .child(line)
                    .into_any_element()
            }
            Entry::Need(key) => match self.data.needs.get(&key) {
                Some(need) => need_card(
                    need,
                    &self.questions,
                    &self.data,
                    self.feed.read(cx),
                    now,
                    cx,
                )
                .into_any_element(),
                None => Empty.into_any_element(),
            },
            Entry::Run(key) => match self.data.runs.get(&key) {
                Some(run) => {
                    self.drew_running |= !is_terminal(run);
                    self.run_row(run, cx.weak_entity()).into_any_element()
                }
                None => Empty.into_any_element(),
            },
            Entry::Session(key) => {
                let session = SessionKey {
                    machine: key.machine.clone(),
                    id: key.id.clone(),
                };
                match self.data.session(&session) {
                    Some(session) => {
                        self.drew_running |= session.state == "running";
                        let body = Self::session_body(cx.weak_entity(), key.clone());
                        session_entry(
                            session,
                            &self.data,
                            self.feed.read(cx),
                            &self.session_drops,
                            body,
                            now,
                            cx,
                        )
                    }
                    None => Empty.into_any_element(),
                }
            }
        };
        self.hold_clock_if_needed(cx);
        // What an offline machine last said stays readable, dimmed, and its actions refuse.
        let dim = self
            .entries
            .get(index)
            .and_then(row_machine)
            .is_some_and(|machine| offline_reason(machine, self.feed.read(cx), cx).is_some());
        div()
            .pb_2()
            .when(dim, |row| row.opacity(0.6))
            .child(row)
            .into_any_element()
    }
}

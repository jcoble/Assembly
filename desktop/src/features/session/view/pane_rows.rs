//! The session pane's rows: each entry of the timeline drawn from the view, the folds and what each item keeps.

use gpui_kit::component::ActiveTheme as _;
use gpui_kit::*;

use super::SessionPane;
use crate::features::session::model::entries::Entry;
use crate::features::session::model::item::{Item, ItemKind};
use crate::features::session::model::markdown_text::markdown_of;
use crate::features::session::model::turn_line::{checkout_line, turn_line};
use crate::features::session::view::item_row::ItemRow;
use crate::features::session::view::session_action::OnAction;
use crate::features::session::view::turn_fold_row::TurnFoldRow;

impl SessionPane {
    pub(super) fn row(&self, entry: &Entry, on_action: &OnAction, cx: &App) -> AnyElement {
        let muted = cx.theme().muted_foreground;
        let now = chrono::Utc::now().timestamp_millis();
        match entry {
            Entry::Loading => line("Loading older messages…", muted),
            Entry::Item(at) => {
                let item = &self.view.items[*at];
                ItemRow {
                    item: item.clone(),
                    kept: self.kept.get(&item.key).cloned().unwrap_or_default(),
                    session: self.session.clone().unwrap_or_default().into(),
                    copy: self.copy_text(item),
                    checkout: self.checkout(item),
                    now,
                    on_action: on_action.clone(),
                }
                .into_any_element()
            }
            Entry::FoldHeading { run_id, .. } => match self.folds.get(run_id) {
                Some(fold) => div()
                    .w_full()
                    .px_4()
                    .py_1()
                    .child(TurnFoldRow {
                        fold: fold.clone(),
                        open: self.open_folds.contains(run_id),
                        on_action: on_action.clone(),
                    })
                    .into_any_element(),
                None => div().into_any_element(),
            },
            Entry::Working => {
                let active = self.view.active_run(self.known_run.as_deref());
                let started = active
                    .as_deref()
                    .and_then(|run| self.view.run_started_at(run))
                    .or(Some(now));
                let stats = active.as_deref().and_then(|run| self.view.stats.get(run));
                line(turn_line(stats, started, now), muted)
            }
        }
    }

    /// A prompt copies its text; a finished turn's final reply copies its markdown. Others have no meta line.
    fn copy_text(&self, item: &Item) -> Option<SharedString> {
        match &item.kind {
            ItemKind::User { text, .. } => Some(text.clone().into()),
            ItemKind::Message { blocks, done, .. } => {
                let fold = item.run_id.as_ref().and_then(|run| self.folds.get(run));
                let last = fold.is_some_and(|fold| fold.reply.as_ref() == Some(&item.key));
                (*done && last).then(|| markdown_of(blocks).into())
            }
            _ => None,
        }
    }

    fn checkout(&self, item: &Item) -> Option<String> {
        if !matches!(item.kind, ItemKind::User { .. }) {
            return None;
        }
        checkout_line(
            item.run_id
                .as_ref()
                .and_then(|run| self.view.stats.get(run)),
        )
    }
}

fn line(text: impl Into<SharedString>, color: Hsla) -> AnyElement {
    div()
        .w_full()
        .px_4()
        .py_1()
        .text_xs()
        .text_color(color)
        .child(text.into())
        .into_any_element()
}

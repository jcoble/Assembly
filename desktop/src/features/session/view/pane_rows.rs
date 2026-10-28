//! The session pane's rows: each entry of the timeline drawn from the view, the folds and what each item keeps.

use std::time::Instant;

use gpui_kit::component::ActiveTheme as _;
use gpui_kit::*;

use super::SessionPane;
use crate::features::session::model::activity;
use crate::features::session::model::entries::Entry;
use crate::features::session::model::item::{Item, ItemKind};
use crate::features::session::model::markdown_text::markdown_of;
use crate::features::session::model::turn_line::checkout_line;
use crate::features::session::view::activity_card::ActivityCard;
use crate::features::session::view::ask_row::AskForm;
use crate::features::session::view::item_row::ItemRow;
use crate::features::session::view::session_action::OnAction;
use crate::features::session::view::tool_row::ToolRow;
use crate::features::session::view::turn_fold_row::TurnFoldRow;

impl SessionPane {
    /// For an item in an open fold's work: its key, whether the fold is opening (or shutting), and when it was toggled.
    pub(super) fn open_fold_of(&self, entry: &Entry) -> Option<(String, bool, Option<Instant>)> {
        let Entry::Item(at) = entry else { return None };
        let key = &self.view.items.get(*at)?.key;
        let run = self.open_folds.iter().find(|run| {
            self.folds
                .get(*run)
                .is_some_and(|fold| fold.work.contains(key))
        })?;
        let kept = self.kept.get(&format!("fold:{run}"))?;
        Some((key.clone(), kept.open, kept.toggled_at))
    }

    pub(super) fn row(&self, entry: &Entry, on_action: &OnAction, cx: &App) -> AnyElement {
        let muted = cx.theme().muted_foreground;
        let now = self
            .now
            .unwrap_or_else(|| chrono::Utc::now().timestamp_millis());
        match entry {
            Entry::Loading => line("Loading older messages…", muted),
            Entry::Item(at) => {
                let item = &self.view.items[*at];
                let mut shown = item.clone();
                if let ItemKind::Message { blocks, .. } = &mut shown.kind
                    && let Some(cut) = self.shown_blocks(&item.key, blocks)
                {
                    *blocks = cut;
                }
                ItemRow {
                    item: shown,
                    kept: self.kept.get(&item.key).cloned().unwrap_or_default(),
                    session: self.session.clone().unwrap_or_default().into(),
                    copy: self.copy_text(item),
                    checkout: self.checkout(item),
                    form: match &item.kind {
                        ItemKind::Ask { ask_id, .. } => self.forms.get(ask_id).cloned(),
                        _ => None,
                    },
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
                        // Where it's going: a fold shutting is still on the list until it's done.
                        open: self
                            .kept
                            .get(&format!("fold:{run_id}"))
                            .is_some_and(|kept| kept.open),
                        toggled_at: self
                            .kept
                            .get(&format!("fold:{run_id}"))
                            .and_then(|kept| kept.toggled_at),
                        on_action: on_action.clone(),
                    })
                    .into_any_element(),
                None => div().into_any_element(),
            },
            Entry::Working => {
                let Some((run, since)) = self.live_since.clone() else {
                    return div().into_any_element();
                };
                let steps = activity::steps(&self.view.items, &run, since, now);
                let footer = activity::footer(steps.len(), now - since);
                let steps = steps
                    .into_iter()
                    .filter_map(|step| {
                        let item = &self.view.items[step.at];
                        let ItemKind::Tool { tool, .. } = &item.kind else {
                            return None;
                        };
                        let row = ToolRow {
                            key: item.key.clone(),
                            tool: tool.clone(),
                            kept: self.kept.get(&item.key).cloned().unwrap_or_default(),
                            took_ms: Some(step.took_ms),
                            on_action: on_action.clone(),
                        };
                        Some(row.into_any_element())
                    })
                    .collect();
                ActivityCard { steps, footer }.into_any_element()
            }
        }
    }

    /// Makes an open question's form the first time its card is drawn: its text fields need the window.
    pub(super) fn make_form(&mut self, entry: &Entry, window: &mut Window, cx: &mut App) {
        let Entry::Item(at) = entry else { return };
        let Some(ItemKind::Ask { ask_id, ask }) = self.view.items.get(*at).map(|item| &item.kind)
        else {
            return;
        };
        if ask.state != "open" || self.forms.contains_key(ask_id) {
            return;
        }
        if let Some(form) = AskForm::of(ask, window, cx) {
            self.forms.insert(ask_id.clone(), form);
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

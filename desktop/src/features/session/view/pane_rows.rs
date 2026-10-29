//! The session pane's rows: each entry of the timeline drawn from the view, the folds and what each item keeps.

use std::time::Instant;

use gpui_kit::component::ActiveTheme as _;
use gpui_kit::*;

use super::SessionPane;
use crate::features::session::model::activity;
use crate::features::session::model::entries::Entry;
use crate::features::session::model::item::{Item, ItemKind};
use crate::features::session::model::markdown_text::markdown_of;
use crate::features::session::model::turn_line::{checkout_line, turn_line};
use crate::features::session::view::activity_card::ActivityCard;
use crate::features::session::view::ask_row::AskForm;
use crate::features::session::view::item_row::{CHAT_COLUMN, ItemRow};
use crate::features::session::view::plan_card::current_step;
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

    pub(super) fn row(&mut self, entry: &Entry, cx: &mut Context<Self>) -> AnyElement {
        let on_action = &self.on_action.clone();
        let muted = cx.theme().muted_foreground;
        let now = self
            .now
            .unwrap_or_else(|| chrono::Utc::now().timestamp_millis());
        match entry {
            Entry::Loading => line("Loading older messages…", muted),
            Entry::Item(at) => {
                // Only a row that opens in place needs its own copy, for the pane's `&mut` while it's made.
                let opens = matches!(
                    self.view.items[*at].kind,
                    ItemKind::Tool { .. } | ItemKind::Thinking { .. } | ItemKind::Ask { .. }
                );
                let expand = opens
                    .then(|| self.view.items[*at].clone())
                    .and_then(|item| self.expand_of(&item, None, cx));
                let item = &self.view.items[*at];
                if self.line_moved(item) {
                    return div().into_any_element();
                }
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
                    turn: self.turn_of(item, now),
                    checkout: self.checkout(item),
                    expand,
                    now,
                    on_action: on_action.clone(),
                }
                .into_any_element()
            }
            Entry::FoldHeading { run_id, .. } => match self.folds.get(run_id) {
                Some(fold) => div()
                    .w_full()
                    .max_w(CHAT_COLUMN)
                    .mx_auto()
                    .px_4()
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
                let plan = (self.view.turn_plan()).map(|items| (current_step(items), items.len()));
                let footer = activity::footer(steps.len(), plan, now - since);
                let steps = steps
                    .into_iter()
                    .filter_map(|step| {
                        let item = self.view.items[step.at].clone();
                        let tool = matches!(item.kind, ItemKind::Tool { .. });
                        tool.then(|| self.expand_of(&item, Some(step.took_ms), cx))?
                    })
                    .collect();
                (div().w_full().max_w(CHAT_COLUMN).mx_auto())
                    .child(ActivityCard { steps, footer })
                    .into_any_element()
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

    /// The space under a row: 5 between a turn's steps and under a shut turn's line (the mock's 27 px `.tl` pitch; a
    /// reply adds its own 9 over it), 14 after a reply or a card, 24 after a turn's stats line. A row with a meta line
    /// (a prompt, a final reply) has that line, shown on hover, as its space.
    pub(super) fn space_under(&self, entry: &Entry) -> Pixels {
        let item = match entry {
            Entry::Item(at) => &self.view.items[*at],
            Entry::FoldHeading { run_id, .. } => {
                let open = self
                    .kept
                    .get(&format!("fold:{run_id}"))
                    .is_some_and(|kept| kept.open);
                return px(if open { 8. } else { 5. });
            }
            Entry::Loading => return px(8.),
            Entry::Working => return px(14.),
        };
        if self.has_meta(item) {
            return px(0.);
        }
        px(match &item.kind {
            ItemKind::Tool { .. } | ItemKind::Thinking { .. } | ItemKind::Other { .. } => 5.,
            ItemKind::Stats { .. } if self.line_moved(item) => 0.,
            ItemKind::Stats { .. } => 24.,
            _ => 14.,
        })
    }

    /// A prompt and a finished turn's final reply have a meta line; other rows don't.
    fn has_meta(&self, item: &Item) -> bool {
        match &item.kind {
            ItemKind::User { .. } => true,
            ItemKind::Message { done, .. } => {
                let fold = item.run_id.as_ref().and_then(|run| self.folds.get(run));
                *done && fold.is_some_and(|fold| fold.reply.as_ref() == Some(&item.key))
            }
            _ => false,
        }
    }

    /// A prompt copies its text; a finished turn's final reply copies its markdown.
    fn copy_text(&self, item: &Item) -> Option<SharedString> {
        match &item.kind {
            _ if !self.has_meta(item) => None,
            ItemKind::User { text, .. } => Some(text.clone().into()),
            ItemKind::Message { blocks, .. } => Some(markdown_of(blocks).into()),
            _ => None,
        }
    }

    /// A completed turn's line that shows under its finished final reply instead (`turn_of`); its row draws nothing.
    fn line_moved(&self, item: &Item) -> bool {
        let ItemKind::Stats { stats } = &item.kind else {
            return false;
        };
        let fold = self.folds.get(&stats.run_id);
        stats.status == "completed" && fold.is_some_and(|fold| fold.reply_done(&self.view.items))
    }

    /// A reply's completed turn's line, for its meta line; the turn's own row then draws nothing.
    fn turn_of(&self, item: &Item, now: i64) -> Option<String> {
        if !matches!(item.kind, ItemKind::Message { .. }) {
            return None;
        }
        let stats = self.view.stats.get(item.run_id.as_ref()?)?;
        (stats.status == "completed").then(|| turn_line(Some(stats), None, now))
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
        .max_w(CHAT_COLUMN)
        .mx_auto()
        .px_4()
        .text_xs()
        .text_color(color)
        .child(text.into())
        .into_any_element()
}

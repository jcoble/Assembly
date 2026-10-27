//! The session pane's rows: each entry of the timeline drawn from the view, the folds and what each item keeps.

use gpui_kit::component::ActiveTheme as _;
use gpui_kit::*;

use super::SessionPane;
use crate::features::session::model::entries::Entry;
use crate::features::session::model::item::{Item, ItemKind};
use crate::features::session::model::markdown_text::markdown_of;
use crate::features::session::model::turn_line::{checkout_line, turn_line};
use crate::features::session::view::ask_card::AskForm;
use crate::features::session::view::item_row::ItemRow;
use crate::features::session::view::session_action::OnAction;
use crate::features::session::view::turn_fold_row::TurnFoldRow;

impl SessionPane {
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
                        open: self.open_folds.contains(run_id),
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
                let (run, since) = self.live_since.clone().unzip();
                let stats = run.and_then(|run| self.view.stats.get(&run));
                line(turn_line(stats, since.or(Some(now)), now), muted)
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

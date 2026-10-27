//! `ItemRow` draws one item of the timeline by its kind: a prompt, a reply with its meta line, thinking, a tool, an
//! ask, a task, a subagent, a note, the line under a turn, an error notice, or a kind it doesn't know by name.
//! Replaces TimelineItem.svelte and the spike's item_view.rs.

use gpui_kit::component::{ActiveTheme as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::ask_card::{AskCard, AskForm};
use super::markdown_view::MarkdownView;
use super::message_meta::MessageMeta;
use super::sent_message::SentMessage;
use super::session_action::{OnAction, SessionAction};
use super::subagent_card::SubagentCard;
use super::task_card::TaskCard;
use super::tool_row::ToolRow;
use crate::features::session::composer::attachment::Attachment;
use crate::features::session::model::item::{Item, ItemKind};
use crate::features::session::model::kept::Kept;
use crate::features::session::model::notices::Notice;
use crate::features::session::model::turn_line::turn_line;

#[derive(IntoElement)]
pub struct ItemRow {
    pub item: Item,
    pub kept: Kept,
    /// The session the item is in, for its file links.
    pub session: SharedString,
    /// What the copy button under a prompt or a final reply copies; `None` draws no meta line.
    pub copy: Option<SharedString>,
    /// "Checkout changed · main@1a2b3c4 → feat@5d6e7f8" above a prompt whose run started elsewhere.
    pub checkout: Option<String>,
    /// An open question's form.
    pub form: Option<AskForm>,
    pub now: i64,
    pub on_action: OnAction,
}

impl RenderOnce for ItemRow {
    fn render(self, _window: &mut Window, cx: &mut App) -> impl IntoElement {
        let theme = cx.theme();
        let (muted, danger) = (theme.muted_foreground, theme.danger);
        let key = self.item.key.clone();
        let meta = |at: i64, copy: Option<SharedString>| {
            copy.map(|copy| MessageMeta {
                key: key.clone(),
                copy,
                at,
                now: self.now,
            })
        };
        let body = match self.item.kind {
            ItemKind::User { text, at, from } => {
                let (text, attached) = Attachment::split_lines(&text);
                let images = attached
                    .into_iter()
                    .map(|(path, name, _)| (path, name))
                    .collect();
                v_flex()
                    .items_end()
                    .gap_1()
                    .when_some(self.checkout.clone(), |column, checkout| {
                        column.child(div().w_full().text_xs().text_color(muted).child(checkout))
                    })
                    .child(SentMessage {
                        key: key.clone(),
                        text,
                        sender: from,
                        images,
                        open: self.kept.open,
                        on_action: self.on_action.clone(),
                    })
                    .children(meta(at, self.copy.clone()))
                    .into_any_element()
            }
            ItemKind::Message { blocks, at, .. } => v_flex()
                .gap_1()
                .child(MarkdownView {
                    id: key.clone().into(),
                    blocks,
                    session: self.session.clone(),
                })
                .children(meta(at, self.copy.clone()))
                .into_any_element(),
            ItemKind::Thinking { text, done } => {
                let on_action = self.on_action.clone();
                let toggle_key = key.clone();
                v_flex()
                    .gap_1()
                    .text_sm()
                    .text_color(muted)
                    .child(
                        h_flex()
                            .id(SharedString::from(format!("{key}:toggle")))
                            .gap_2()
                            .cursor_pointer()
                            .on_click(move |_, window, cx| {
                                let key = toggle_key.clone();
                                on_action(SessionAction::Toggle { key }, window, cx)
                            })
                            .child(if self.kept.open { "▾" } else { "▸" })
                            .child(if done { "Thought" } else { "Thinking" }),
                    )
                    .when(self.kept.open, |column| {
                        column.child(div().italic().child(text))
                    })
                    .into_any_element()
            }
            ItemKind::Tool { tool, .. } => ToolRow {
                key: key.clone(),
                tool,
                kept: self.kept,
                on_action: self.on_action.clone(),
            }
            .into_any_element(),
            ItemKind::Error { message, code } => {
                match Notice::of_error(&message, code.as_deref()) {
                    Some(notice) => v_flex()
                        .gap_1()
                        .text_sm()
                        .child(
                            div()
                                .font_weight(FontWeight::MEDIUM)
                                .text_color(danger)
                                .child(notice.label),
                        )
                        .child(notice.message)
                        .when_some(notice.hint, |column, hint| {
                            column.child(div().text_color(muted).child(hint))
                        })
                        .into_any_element(),
                    None => div().into_any_element(),
                }
            }
            ItemKind::Ask { ask_id, ask } => AskCard {
                ask_id,
                ask,
                form: self.form,
                on_action: self.on_action.clone(),
            }
            .into_any_element(),
            ItemKind::Task { task_id, task } => TaskCard {
                task_id,
                task,
                on_action: self.on_action.clone(),
            }
            .into_any_element(),
            ItemKind::Subagent { subagent, .. } => SubagentCard {
                subagent,
                on_action: self.on_action.clone(),
            }
            .into_any_element(),
            ItemKind::Note { text } => div()
                .text_sm()
                .italic()
                .text_color(muted)
                .child(text)
                .into_any_element(),
            ItemKind::Stats { stats } => v_flex()
                .text_xs()
                .text_color(muted)
                .child(turn_line(Some(&stats), None, self.now))
                .into_any_element(),
            ItemKind::Other { kind } => div()
                .text_xs()
                .text_color(muted)
                .child(format!("[{kind}]"))
                .into_any_element(),
        };
        div().w_full().px_4().py_1().child(body)
    }
}

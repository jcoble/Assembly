//! `ItemRow` draws one item of the timeline by its kind: a prompt, a reply with its meta line, thinking, a tool, an
//! ask, a task, a subagent, a note, the line under a stopped or failed turn, an error notice, or a kind it doesn't
//! know by name. Replaces TimelineItem.svelte and the spike's item_view.rs.

use gpui_kit::component::{ActiveTheme as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::grow::chevron;
use super::markdown_view::MarkdownView;
use super::message_meta::MessageMeta;
use super::sent_message::SentMessage;
use super::session_action::OnAction;
use super::subagent_card::SubagentCard;
use super::task_card::TaskCard;
use crate::features::session::composer::attachment::Attachment;
use crate::features::session::model::item::{Item, ItemKind};
use crate::features::session::model::kept::Kept;
use crate::features::session::model::notices::Notice;
use crate::features::session::model::turn_line::turn_line;

/// The chat column's widest, centred in the pane (the reading measure); the composer keeps to it too.
pub const CHAT_COLUMN: Pixels = px(700.);

#[derive(IntoElement)]
pub struct ItemRow {
    pub item: Item,
    pub kept: Kept,
    /// The session the item is in, for its file links.
    pub session: SharedString,
    /// What the copy button under a prompt or a final reply copies; `None` draws no meta line.
    pub copy: Option<SharedString>,
    /// A final reply's turn line ("Worked for 42s · Thu 12:02 PM · model · …"), shown in its meta line.
    pub turn: Option<String>,
    /// "Checkout changed · main@1a2b3c4 → feat@5d6e7f8" above a prompt whose run started elsewhere.
    pub checkout: Option<String>,
    /// What the pane drew for a tool call, thinking or a question: the `ExpandRow` it keeps for it (pane_expand.rs).
    pub expand: Option<AnyElement>,
    pub now: i64,
    pub on_action: OnAction,
}

impl RenderOnce for ItemRow {
    fn render(self, _window: &mut Window, cx: &mut App) -> impl IntoElement {
        let theme = cx.theme();
        let (muted, danger) = (theme.muted_foreground, theme.danger);
        let key = self.item.key.clone();
        // Each row its own hover group, so its copy button and time show only under the pointer.
        let group = SharedString::from(format!("{key}:meta"));
        let meta = |at: i64, copy: Option<SharedString>, turn: Option<String>| {
            copy.map(|copy| MessageMeta {
                key: key.clone(),
                group: group.clone(),
                copy,
                at,
                turn,
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
                    .children(meta(at, self.copy.clone(), None))
                    .into_any_element()
            }
            ItemKind::Message { blocks, at, .. } => v_flex()
                .gap_1()
                .child(MarkdownView {
                    id: key.clone().into(),
                    blocks,
                    session: self.session.clone(),
                })
                .children(meta(at, self.copy.clone(), self.turn.clone()))
                .into_any_element(),
            ItemKind::Thinking { .. } | ItemKind::Tool { .. } | ItemKind::Ask { .. } => {
                self.expand.unwrap_or_else(|| div().into_any_element())
            }
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
            // A completed turn's line shows on hover under its final reply instead; Stopped and Failed stay shown.
            ItemKind::Stats { stats } if stats.status == "completed" => div().into_any_element(),
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
        div()
            .group(group.clone())
            .w_full()
            .max_w(CHAT_COLUMN)
            .mx_auto()
            .px_4()
            // The reading style, 14 on 22; a line that sets its own size keeps the same proportion.
            .text_size(px(14.))
            .line_height(relative(22. / 14.))
            .child(body)
    }
}

/// Thinking's line: "Thinking" until it's done, then "Thought", and a chevron after it turned `grown` of the way.
pub fn thinking_title(done: bool, grown: f32, cx: &App) -> AnyElement {
    h_flex()
        .h(px(22.))
        .gap_2()
        .text_size(px(13.))
        .text_color(cx.theme().muted_foreground)
        .child(if done { "Thought" } else { "Thinking" })
        .child(chevron(grown))
        .into_any_element()
}

/// What thinking shows open: its text, hanging from a rule under the title.
pub fn thinking_body(text: SharedString, cx: &App) -> AnyElement {
    let theme = cx.theme();
    div()
        .ml(px(6.))
        .pl(px(16.))
        .border_l_1()
        .border_color(theme.border)
        .text_sm()
        .text_color(theme.muted_foreground)
        .child(text)
        .into_any_element()
}

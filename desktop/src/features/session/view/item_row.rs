//! `ItemRow` draws one item of the timeline by its kind: a prompt, a reply with its meta line, thinking, a tool, an
//! ask, a task, a subagent, a note, the line under a turn, an error notice, or a kind it doesn't know by name.
//! Replaces TimelineItem.svelte and the spike's item_view.rs.

use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::core::ui::access::ExposedElement as _;
use crate::core::ui::access::control_ids;
use gpui_kit::assets::IconName as Glyph;
use gpui_kit::component::{Icon, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use crate::core::state::preferences::Preferences;
use crate::core::theme::look::Look;
use crate::core::theme::metrics::{CORNER_TILE, SPACE};

use super::grow::chevron;
use super::message_meta::MessageMeta;
use super::sent_message::SentMessage;
use super::session_action::OnAction;
use super::subagent_card::SubagentCard;
use super::task_card::TaskCard;
use crate::core::ui::markdown::view::MarkdownView;
use crate::features::session::composer::attachment::Attachment;
use crate::features::session::model::item::{Item, ItemKind};
use crate::features::session::model::kept::Kept;
use crate::features::session::model::markdown_text::markdown_of;
use crate::features::session::model::notices::Notice;
use crate::features::session::model::turn_line::turn_line;

/// The chat column's widest, centred in the pane (the reading measure). With its 16 px sides its content is the look's
/// 704 px `.col` (metrics::TEXT_COLUMN_WIDTH).
pub const CHAT_COLUMN: Pixels = px(736.);
/// The error bar's alert and thinking's bulb.
const ERROR_ICON: Pixels = px(16.);
const THINKING_ICON: Pixels = px(14.);

#[derive(IntoElement)]
pub struct ItemRow {
    pub item: Item,
    pub kept: Kept,
    /// The session the item is in, for its file links.
    pub session: SharedString,
    pub expected_source: Option<crate::core::socket::row_source::ExpectedRowSource>,
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
        let look = Look::of(cx);
        let (chat_size, chat_height) =
            Preferences::read(cx, |appearance| appearance.ui_text(TextRole::Chat));
        let muted = look.text_3;
        let key = self.item.key.clone();
        // Each row its own hover group, so its copy button and time show only under the pointer.
        let group = SharedString::from(format!("{key}:meta"));
        let meta = |at: i64, copy: Option<SharedString>, turn: Option<String>, reply: bool| {
            copy.map(|copy| MessageMeta {
                key: key.clone(),
                group: group.clone(),
                copy,
                at,
                turn,
                now: self.now,
                reply,
            })
        };
        let body = match self.item.kind {
            ItemKind::User {
                text,
                at,
                from,
                from_machine,
                sender_label,
            } => {
                let (text, attached) = Attachment::split_lines(&text);
                let images = attached
                    .into_iter()
                    .map(|(path, name, _)| (path, name))
                    .collect();
                v_flex()
                    .items_end()
                    .gap_1()
                    .when_some(self.checkout.clone(), |column, checkout| {
                        column.child(
                            div()
                                .w_full()
                                .text_role(TextRole::Meta, cx)
                                .text_color(muted)
                                .child(checkout),
                        )
                    })
                    .child(SentMessage {
                        key: key.clone(),
                        text,
                        sender: from,
                        sender_machine: from_machine,
                        sender_label,
                        images,
                        open: self.kept.open,
                        on_action: self.on_action.clone(),
                    })
                    .children(meta(at, self.copy.clone(), None, false))
                    .into_any_element()
            }
            // The mock's `.reply` sits 14 px under the line above it: 9 more than a step's 5.
            ItemKind::Message { blocks, at, .. } => v_flex()
                .pt(px(9.))
                .gap_1()
                // The reply in text-1.
                .child(div().text_color(look.text_1).child(MarkdownView {
                    id: key.clone().into(),
                    blocks,
                    session: Some(self.session.clone()),
                    expected_source: self.expected_source.clone(),
                }))
                .children(meta(at, self.copy.clone(), self.turn.clone(), true))
                .into_any_element(),
            ItemKind::Thinking { .. } | ItemKind::Tool { .. } | ItemKind::Ask { .. } => {
                self.expand.unwrap_or_else(|| div().into_any_element())
            }
            ItemKind::Error { message, code } => {
                match Notice::of_error(&message, code.as_deref()) {
                    // The look's error bar: a card with the problem line, its alert icon, the label in the
                    // problem colour ahead of the message, and any hint under them in text-3.
                    Some(notice) => h_flex()
                        .items_start()
                        .gap(SPACE[4])
                        .py(SPACE[3])
                        .pl(SPACE[6])
                        .pr(SPACE[3])
                        .rounded(CORNER_TILE)
                        .border_1()
                        .border_color(look.problem_line)
                        .text_role(TextRole::Body, cx)
                        .child(
                            Icon::new(Glyph::TriangleAlert)
                                .size(ERROR_ICON)
                                .flex_none()
                                .mt(SPACE[0])
                                .text_color(look.problem),
                        )
                        .child(
                            v_flex()
                                .min_w_0()
                                .child(
                                    h_flex()
                                        .flex_wrap()
                                        .gap(SPACE[1])
                                        .text_color(look.text_1)
                                        .child(
                                            div()
                                                .font_weight(FontWeight::SEMIBOLD)
                                                .text_color(look.problem)
                                                .child(notice.label),
                                        )
                                        .child(notice.message),
                                )
                                .when_some(notice.hint, |column, hint| {
                                    column.child(
                                        div()
                                            .text_role(TextRole::Meta, cx)
                                            .text_color(muted)
                                            .child(hint),
                                    )
                                }),
                        )
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
                key: key.clone(),
                subagent,
                on_action: self.on_action.clone(),
            }
            .into_any_element(),
            ItemKind::Note { text } => div()
                .text_role(TextRole::Body, cx)
                .italic()
                .text_color(muted)
                .child(text)
                .into_any_element(),
            ItemKind::Stats { stats } => v_flex()
                .text_role(TextRole::Meta, cx)
                .text_color(muted)
                .child(turn_line(Some(&stats), None, self.now))
                .into_any_element(),
            ItemKind::Other { kind } => div()
                .text_role(TextRole::Meta, cx)
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
            // The reading style, the mock's `.reply`: 13.5 px at 1.6; a line that sets its own size keeps the proportion.
            .text_size(chat_size)
            .line_height(chat_height)
            .child(body)
    }
}

/// Thinking's line, as a tool line: a bulb, "Thinking" until it's done, then "Thought", and a chevron after it turned
/// `grown` of the way, all in text-3 at Meta, as the look's `.thl`.
pub fn thinking_title(done: bool, grown: f32, cx: &App) -> AnyElement {
    let faint = Look::of(cx).text_3;
    h_flex()
        .h(px(22.))
        .gap(SPACE[2])
        .text_role(TextRole::Meta, cx)
        .text_color(faint)
        .child(Icon::new(Glyph::Lightbulb).size(THINKING_ICON))
        .child(if done { "Thought" } else { "Thinking" })
        .child(chevron(grown))
        .into_any_element()
}

/// What thinking shows open: its text in text-2, hanging from a 2px rule, as the look's `.th`.
pub fn thinking_body(text: SharedString, cx: &App) -> AnyElement {
    let look = Look::of(cx);
    div()
        .ml(px(7.))
        .pl(px(13.))
        .border_l_2()
        .border_color(look.hairline_strong)
        .text_role(TextRole::Body, cx)
        .text_color(look.text_2)
        .child(text)
        .into_any_element()
}

// How a chat row reaches the accessibility tree: a list item found by its item's key (`rows.tool.<id>`), read as its
// kind and the start of its text, so a script or an assistive client can read the chat and find a row. The virtual
// list draws only the rows on screen, so only those are in the tree.

/// How much of a row's text its label carries.
const LABEL_CHARS: usize = 80;

/// `"<kind>: <text>"`, the text on one line and cut to 80 characters.
pub fn row_label(item: &Item) -> String {
    let (kind, text) = match &item.kind {
        ItemKind::User { text, .. } => ("user", text.clone()),
        ItemKind::Message { blocks, .. } => ("message", markdown_of(blocks)),
        ItemKind::Thinking { text, .. } => ("thinking", text.clone()),
        ItemKind::Tool { tool, .. } => (
            "tool",
            tool.summary.clone().unwrap_or_else(|| tool.name.clone()),
        ),
        ItemKind::Error { message, .. } => ("error", message.clone()),
        ItemKind::Note { text } => ("note", text.clone()),
        ItemKind::Ask { .. } => ("ask", String::new()),
        ItemKind::Task { .. } => ("task", String::new()),
        ItemKind::Subagent { .. } => ("subagent", String::new()),
        ItemKind::Stats { .. } => ("stats", String::new()),
        ItemKind::Other { kind } => (kind.as_str(), String::new()),
    };
    let line = text.split_whitespace().collect::<Vec<_>>().join(" ");
    let mut cut: String = line.chars().take(LABEL_CHARS).collect();
    if cut.len() < line.len() {
        cut.push('…');
    }
    format!("{kind}: {cut}")
}

/// The div a chat row is drawn in, named in the tree by its item's `key`.
pub fn chat_row(key: &str, label: String) -> Stateful<Div> {
    div().id(SharedString::from(key.to_string())).exposed(
        Role::ListItem,
        control_ids::row(key),
        label,
    )
}

#[cfg(test)]
#[path = "tests/chat_row_tests.rs"]
mod chat_row_tests;

//! How a chat row reaches the accessibility tree: a list item found by its item's key (`rows.tool.<id>`), read as its
//! kind and the start of its text, so a script or an assistive client can read the chat and find a row. The virtual
//! list draws only the rows on screen, so only those are in the tree.

use gpui_kit::*;

use crate::core::ui::access::ExposedElement as _;
use crate::core::ui::access::control_ids;
use crate::features::session::model::item::{Item, ItemKind};
use crate::features::session::model::markdown_text::markdown_of;

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
mod tests;

//! `Block` is a message's markdown as the backend parsed and cleaned it (SafeMarkdownBlock), with its inline parts.
//! A complete code block carries its highlighted lines.

use serde::Deserialize;

#[derive(Clone, Debug, PartialEq, Deserialize)]
#[serde(tag = "kind", rename_all = "kebab-case")]
pub enum Block {
    Paragraph { parts: Vec<Inline> },
    Heading { level: u8, parts: Vec<Inline> },
    Code { language: String, value: String, complete: bool, lines: Option<Vec<Vec<CodeToken>>> },
    Quote { blocks: Vec<Block> },
    List { ordered: bool, items: Vec<ListItem> },
    Table { headers: Vec<Vec<Inline>>, rows: Vec<Vec<Vec<Inline>>> },
    Rule,
}

#[derive(Clone, Debug, PartialEq, Deserialize)]
#[serde(tag = "kind", rename_all = "kebab-case")]
pub enum Inline {
    Text { value: String },
    Code { value: String },
    Strong { parts: Vec<Inline> },
    Emphasis { parts: Vec<Inline> },
    Strike { parts: Vec<Inline> },
    Link { href: String, parts: Vec<Inline> },
    FileLink { path: String, parts: Vec<Inline> },
}

#[derive(Clone, Debug, PartialEq, Deserialize)]
pub struct ListItem {
    pub task: bool,
    pub checked: bool,
    pub parts: Vec<Inline>,
    pub blocks: Vec<Block>,
}

/// One highlighted piece of a code line; `class_name` is keyword, string, comment, number, type or plain.
#[derive(Clone, Debug, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CodeToken {
    pub value: String,
    pub class_name: String,
}

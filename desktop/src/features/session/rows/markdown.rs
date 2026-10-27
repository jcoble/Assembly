//! `Block` is a message's markdown as the backend parsed and cleaned it (SafeMarkdownBlock), with its inline parts.
//! The backend doesn't highlight code yet, so a code block is its text.

use serde::Deserialize;

#[derive(Clone, Debug, PartialEq, Deserialize)]
#[serde(tag = "kind", rename_all = "kebab-case")]
pub enum Block {
    Paragraph {
        parts: Vec<Inline>,
    },
    Heading {
        level: u8,
        parts: Vec<Inline>,
    },
    Code {
        language: String,
        value: String,
        complete: bool,
    },
    Quote {
        blocks: Vec<Block>,
    },
    List {
        ordered: bool,
        items: Vec<ListItem>,
    },
    Table {
        headers: Vec<Vec<Inline>>,
        rows: Vec<Vec<Vec<Inline>>>,
    },
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

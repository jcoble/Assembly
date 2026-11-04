//! A task's Notion blocks as rows to draw. Each row carries its markdown, so the kit's text view lays it out, and the
//! block's id and plain text, so it can be edited. Children are indented, and a toggle's show only while it's open.

use std::collections::HashSet;

use serde_json::Value;

use super::task_export::NotionBlock;

#[derive(Clone, Debug, PartialEq)]
pub struct BodyRow {
    pub id: String,
    pub kind: String,
    pub depth: usize,
    /// What's drawn. Empty for a row drawn some other way.
    pub markdown: String,
    /// What the editor starts from.
    pub plain: String,
    pub checked: Option<bool>,
    /// The number of a numbered list item.
    pub number: Option<usize>,
    pub icon: Option<String>,
    /// For a toggle: whether it's open, if it has anything to show.
    pub open: Option<bool>,
    pub image: Option<ImageRow>,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ImageRow {
    pub url: Option<String>,
    pub caption: String,
}

/// The name the edit call takes for an editable block, in the backend's list.
pub fn edit_kind(kind: &str) -> Option<&'static str> {
    Some(match kind {
        "paragraph" => "paragraph",
        "heading_1" => "heading1",
        "heading_2" => "heading2",
        "heading_3" => "heading3",
        "bulleted_list_item" => "bullet",
        "numbered_list_item" => "numbered",
        "to_do" => "todo",
        "quote" => "quote",
        "code" => "code",
        _ => return None,
    })
}

pub fn body_rows(blocks: &[NotionBlock], open: &HashSet<String>) -> Vec<BodyRow> {
    let mut rows = Vec::new();
    walk(blocks, open, 0, &mut rows);
    rows
}

fn walk(blocks: &[NotionBlock], open: &HashSet<String>, depth: usize, rows: &mut Vec<BodyRow>) {
    let mut number = 0;
    for block in blocks {
        number = if block.kind == "numbered_list_item" {
            number + 1
        } else {
            0
        };
        let mut row = row_for(block, depth);
        if block.kind == "numbered_list_item" {
            row.number = Some(number);
        }
        if block.kind == "table" {
            rows.push(row);
            continue;
        }
        let hidden = block.kind == "toggle" && !open.contains(&block.id);
        if block.kind == "toggle" {
            row.open = (!block.children.is_empty()).then_some(!hidden);
        }
        rows.push(row);
        if !hidden {
            walk(&block.children, open, depth + 1, rows);
        }
    }
}

fn row_for(block: &NotionBlock, depth: usize) -> BodyRow {
    let content = &block.content;
    let text = rich_markdown(&content["rich_text"]);
    let markdown = match block.kind.as_str() {
        "heading_1" => format!("# {text}"),
        "heading_2" => format!("## {text}"),
        "heading_3" => format!("### {text}"),
        "code" => code_fence(content),
        "table" => table_markdown(block),
        "divider" | "image" => String::new(),
        "paragraph" | "bulleted_list_item" | "numbered_list_item" | "to_do" | "quote"
        | "callout" | "toggle" => text,
        other => format!("*{} isn't shown here.*", other.replace('_', " ")),
    };
    BodyRow {
        id: block.id.clone(),
        kind: block.kind.clone(),
        depth,
        markdown,
        plain: rich_plain(&content["rich_text"]),
        checked: (block.kind == "to_do").then(|| content["checked"].as_bool().unwrap_or(false)),
        number: None,
        icon: content["icon"]["emoji"].as_str().map(str::to_owned),
        open: None,
        image: (block.kind == "image").then(|| image_row(content)),
    }
}

fn image_row(content: &Value) -> ImageRow {
    let url = ["file", "external"]
        .iter()
        .find_map(|kind| content[kind]["url"].as_str())
        .map(str::to_owned);
    ImageRow {
        url,
        caption: rich_plain(&content["caption"]),
    }
}

pub fn rich_plain(rich: &Value) -> String {
    let Some(items) = rich.as_array() else {
        return String::new();
    };
    items.iter().map(item_text).collect()
}

fn item_text(item: &Value) -> &str {
    item["plain_text"]
        .as_str()
        .or_else(|| item["text"]["content"].as_str())
        .unwrap_or_default()
}

/// Notion's rich text as markdown: its text escaped, so a character in it can't change what's drawn, then its marks.
pub fn rich_markdown(rich: &Value) -> String {
    let Some(items) = rich.as_array() else {
        return String::new();
    };
    items.iter().map(item_markdown).collect()
}

fn item_markdown(item: &Value) -> String {
    let raw = item_text(item);
    if raw.is_empty() {
        return String::new();
    }
    // A mark can't hold the spaces around its text, so they stay outside it.
    let core = raw.trim();
    let (lead, tail) = (
        &raw[..raw.len() - raw.trim_start().len()],
        &raw[raw.trim_end().len()..],
    );
    if core.is_empty() {
        return raw.to_owned();
    }
    let marks = &item["annotations"];
    let on = |name: &str| marks[name].as_bool().unwrap_or(false);
    let mut text = if on("code") {
        let fence = "`".repeat(longest_run(core, '`') + 1);
        format!("{fence}{core}{fence}")
    } else {
        escape(core)
    };
    if on("bold") {
        text = format!("**{text}**");
    }
    if on("italic") {
        text = format!("*{text}*");
    }
    if on("strikethrough") {
        text = format!("~~{text}~~");
    }
    if let Some(href) = item["href"].as_str() {
        text = format!(
            "[{text}](<{}>)",
            href.replace('>', "%3E").replace('<', "%3C")
        );
    }
    format!("{lead}{text}{tail}")
}

fn escape(text: &str) -> String {
    let mut out = String::with_capacity(text.len());
    for c in text.chars() {
        if "\\`*_{}[]<>|#!~".contains(c) {
            out.push('\\');
        }
        out.push(c);
    }
    out
}

fn longest_run(text: &str, of: char) -> usize {
    let (mut longest, mut run) = (0, 0);
    for c in text.chars() {
        run = if c == of { run + 1 } else { 0 };
        longest = longest.max(run);
    }
    longest
}

fn code_fence(content: &Value) -> String {
    let text = rich_plain(&content["rich_text"]);
    let fence = "`".repeat((longest_run(&text, '`') + 1).max(3));
    let language = content["language"].as_str().unwrap_or_default();
    let language = if language
        .chars()
        .all(|c| c.is_ascii_alphanumeric() || c == '_' || c == '-')
    {
        language
    } else {
        ""
    };
    format!("{fence}{language}\n{text}\n{fence}")
}

fn table_markdown(block: &NotionBlock) -> String {
    let cells = |row: &NotionBlock| -> Vec<String> {
        row.content["cells"]
            .as_array()
            .map(|cells| cells.iter().map(rich_markdown).collect())
            .unwrap_or_default()
    };
    let rows: Vec<Vec<String>> = block.children.iter().map(cells).collect();
    let Some(width) = rows.iter().map(Vec::len).max().filter(|width| *width > 0) else {
        return String::new();
    };
    let line = |row: &[String]| -> String {
        let mut cells: Vec<&str> = row.iter().map(String::as_str).collect();
        cells.resize(width, "");
        format!("| {} |", cells.join(" | "))
    };
    let rule = format!("| {} |", vec!["---"; width].join(" | "));
    let header = block.content["has_column_header"]
        .as_bool()
        .unwrap_or(false);
    let mut lines = Vec::new();
    let body = if header {
        lines.push(line(&rows[0]));
        &rows[1..]
    } else {
        lines.push(line(&[]));
        &rows[..]
    };
    lines.push(rule);
    lines.extend(body.iter().map(|row| line(row)));
    lines.join("\n")
}

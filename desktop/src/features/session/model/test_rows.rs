//! Rows for the session view's tests: the web view's fixtures, rows made by hand, and the page query's newest form.

use serde_json::{Value, json};

use super::item::ItemKind;
use super::session_view::SessionView;
use crate::features::session::rows::row::Row;

pub fn fixture(name: &str) -> Vec<Row> {
    let path = format!("{}/fixtures/{name}", env!("CARGO_MANIFEST_DIR"));
    let file: Value = serde_json::from_str(&std::fs::read_to_string(path).unwrap()).unwrap();
    serde_json::from_value(file["rows"].clone()).unwrap()
}

pub fn all() -> Vec<Row> {
    [fixture("rows-text.json"), fixture("rows-tools.json")].concat()
}

pub fn live(mut view: SessionView, rows: &[Row]) -> SessionView {
    for row in rows {
        view.apply(row);
    }
    view
}

pub fn keys(view: &SessionView) -> Vec<&str> {
    view.items.iter().map(|item| item.key.as_str()).collect()
}

pub fn row(seq: i64, run: &str, item: Option<&str>, kind: &str, payload: Value) -> Row {
    Row {
        session_id: "t".into(),
        seq,
        run_id: Some(run.into()),
        item_id: item.map(Into::into),
        kind: kind.into(),
        payload,
        created_at: seq,
        first_seq: None,
        prev_seq: None,
    }
}

pub fn group(row: &Row) -> &str {
    if row.kind == "delta" {
        "message"
    } else {
        &row.kind
    }
}

pub fn same_item(a: &Row, b: &Row) -> bool {
    a.item_id.is_some() && a.item_id == b.item_id && group(a) == group(b)
}

/// What the backend's page query answers: the newest row of each (item, kind), a delta hidden once its item has a
/// message row, an unfinished message as one delta from 0 with its blocks so far, and every row carrying its item's
/// first seq, ordered by it.
pub fn newest_form(rows: &[Row]) -> Vec<Row> {
    let mut kept: Vec<Row> = Vec::new();
    for row in rows {
        let first_seq = match row.item_id {
            None => row.seq,
            Some(_) => rows
                .iter()
                .filter(|r| same_item(r, row))
                .map(|r| r.seq)
                .min()
                .unwrap(),
        };
        let newer = rows.iter().any(|r| {
            same_item(r, row) && r.seq > row.seq && (r.kind == row.kind || r.kind == "message")
        });
        if row.item_id.is_some() && newer {
            continue;
        }
        let mut row = Row {
            first_seq: Some(first_seq),
            ..row.clone()
        };
        if row.kind == "delta" {
            let mut blocks: Vec<Value> = Vec::new();
            let mut text = String::new();
            for delta in rows.iter().filter(|r| same_item(r, &row)) {
                blocks.truncate(delta.payload["from"].as_u64().unwrap() as usize);
                blocks.extend(delta.payload["blocks"].as_array().unwrap().iter().cloned());
                text.push_str(delta.payload["text"].as_str().unwrap());
            }
            row.payload = json!({ "from": 0, "text": text, "blocks": blocks });
        }
        kept.push(row);
    }
    kept.sort_by_key(|row| row.first_seq);
    kept
}

pub fn message(view: &SessionView, key: &str) -> (Vec<&'static str>, bool) {
    let item = view.items.iter().find(|item| item.key == key).unwrap();
    let ItemKind::Message { blocks, done, .. } = &item.kind else {
        panic!("not a message")
    };
    let kinds = blocks.iter().map(|block| block_kind(block)).collect();
    (kinds, *done)
}

pub fn block_kind(block: &crate::features::session::rows::markdown::Block) -> &'static str {
    use crate::features::session::rows::markdown::Block;
    match block {
        Block::Paragraph { .. } => "paragraph",
        Block::Heading { .. } => "heading",
        Block::Code { .. } => "code",
        Block::Quote { .. } => "quote",
        Block::List { .. } => "list",
        Block::Table { .. } => "table",
        Block::Rule => "rule",
    }
}

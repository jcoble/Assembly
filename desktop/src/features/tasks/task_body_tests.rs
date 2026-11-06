use std::collections::HashSet;

use serde_json::{Value, json};

use super::task_body::*;
use super::task_export::NotionBlock;

fn block(id: &str, kind: &str, content: Value, children: Vec<NotionBlock>) -> NotionBlock {
    NotionBlock {
        id: id.into(),
        kind: kind.into(),
        content,
        children,
    }
}

fn text(value: &str) -> Value {
    json!({"rich_text":[{"plain_text":value}]})
}

#[test]
fn marks_and_links_become_markdown_and_stray_characters_stay_text() {
    let rich = json!([
        {"plain_text":"a*b ","annotations":{}},
        {"plain_text":"bold","annotations":{"bold":true}},
        {"plain_text":" "},
        {"plain_text":"x`y","annotations":{"code":true}},
        {"plain_text":" site","href":"https://e.com/a>b"},
    ]);
    assert_eq!(
        rich_markdown(&rich),
        "a\\*b **bold** ``x`y`` [site](<https://e.com/a%3Eb>)"
            .replace("[site]", " [site]")
            .replace("  [", " [")
    );
    assert_eq!(rich_plain(&rich), "a*b bold x`y site");
}

#[test]
fn utf8_text_survives_marks_and_edits() {
    let rich = json!([{"plain_text":" héllo 世界 🎉 ","annotations":{"italic":true}}]);
    assert_eq!(rich_markdown(&rich), " *héllo 世界 🎉* ");
    assert_eq!(rich_plain(&rich), " héllo 世界 🎉 ");
}

#[test]
fn numbered_items_count_within_their_run() {
    let rows = body_rows(
        &[
            block("a", "numbered_list_item", text("one"), vec![]),
            block("b", "numbered_list_item", text("two"), vec![]),
            block("c", "paragraph", text("gap"), vec![]),
            block("d", "numbered_list_item", text("again"), vec![]),
        ],
        &HashSet::new(),
    );
    let numbers: Vec<_> = rows.iter().map(|row| row.number).collect();
    assert_eq!(numbers, vec![Some(1), Some(2), None, Some(1)]);
}

#[test]
fn a_toggle_shows_its_children_only_while_open() {
    let blocks = [block(
        "t",
        "toggle",
        text("more"),
        vec![block("c", "paragraph", text("inside"), vec![])],
    )];
    let shut = body_rows(&blocks, &HashSet::new());
    assert_eq!(shut.len(), 1);
    assert_eq!(shut[0].open, Some(false));
    let open = body_rows(&blocks, &HashSet::from(["t".to_string()]));
    assert_eq!(open.len(), 2);
    assert_eq!((open[0].open, open[1].depth), (Some(true), 1));
}

#[test]
fn a_to_do_keeps_its_id_and_check() {
    let rows = body_rows(
        &[block(
            "todo-1",
            "to_do",
            json!({"rich_text":[{"plain_text":"ship"}],"checked":true}),
            vec![],
        )],
        &HashSet::new(),
    );
    assert_eq!(rows[0].id, "todo-1");
    assert_eq!(rows[0].checked, Some(true));
    assert_eq!(rows[0].plain, "ship");
    assert_eq!(edit_kind(&rows[0].kind), Some("todo"));
}

#[test]
fn a_table_is_one_row_of_markdown() {
    let row = |a: &str, b: &str| {
        block(
            "r",
            "table_row",
            json!({"cells":[[{"plain_text":a}],[{"plain_text":b}]]}),
            vec![],
        )
    };
    let rows = body_rows(
        &[block(
            "t",
            "table",
            json!({"has_column_header":true}),
            vec![row("h1", "h2"), row("a", "b")],
        )],
        &HashSet::new(),
    );
    assert_eq!(rows.len(), 1);
    assert_eq!(rows[0].markdown, "| h1 | h2 |\n| --- | --- |\n| a | b |");
}

#[test]
fn an_image_keeps_its_address_and_caption() {
    let rows = body_rows(
        &[block(
            "i",
            "image",
            json!({"type":"file","file":{"url":"https://files/a.png"},"caption":[{"plain_text":"shot"}]}),
            vec![],
        )],
        &HashSet::new(),
    );
    assert_eq!(
        rows[0].image,
        Some(ImageRow {
            url: Some("https://files/a.png".into()),
            caption: "shot".into()
        })
    );
}

#[test]
fn a_block_the_app_cannot_draw_says_so() {
    let rows = body_rows(
        &[block("b", "child_database", json!({}), vec![])],
        &HashSet::new(),
    );
    assert_eq!(rows[0].markdown, "*child database isn't shown here.*");
    assert_eq!(edit_kind("child_database"), None);
}

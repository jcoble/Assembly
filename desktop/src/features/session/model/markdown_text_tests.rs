//! A reply copies as markdown, ported from app/test/fold.test.ts.

use super::markdown_text::markdown_of;
use crate::features::session::rows::markdown::{Block, Inline, ListItem};

fn text(value: &str) -> Inline {
    Inline::Text {
        value: value.into(),
    }
}

// Pins: g0-session#21
#[test]
fn a_reply_copies_as_markdown() {
    let blocks = vec![
        Block::Heading {
            level: 2,
            parts: vec![text("Done")],
        },
        Block::Paragraph {
            parts: vec![
                text("Ran "),
                Inline::Code { value: "ls".into() },
                text(" and "),
                Inline::Strong {
                    parts: vec![text("it worked")],
                },
            ],
        },
        Block::List {
            ordered: false,
            items: vec![ListItem {
                task: false,
                checked: false,
                parts: vec![text("one")],
                blocks: vec![],
            }],
        },
        Block::Code {
            language: "ts".into(),
            value: "x();\n".into(),
            complete: true,
        },
    ];
    assert_eq!(
        markdown_of(&blocks),
        "## Done\n\nRan `ls` and **it worked**\n\n- one\n\n```ts\nx();\n```"
    );
}

#[test]
fn quotes_tables_tasks_and_links_copy_too() {
    let blocks = vec![
        Block::Quote {
            blocks: vec![
                Block::Paragraph {
                    parts: vec![text("a")],
                },
                Block::Paragraph {
                    parts: vec![text("b")],
                },
            ],
        },
        Block::Table {
            headers: vec![vec![text("h1")], vec![text("h2")]],
            rows: vec![vec![vec![text("1")], vec![text("2")]]],
        },
        Block::List {
            ordered: true,
            items: vec![ListItem {
                task: true,
                checked: true,
                parts: vec![Inline::Link {
                    href: "https://x".into(),
                    parts: vec![text("x")],
                }],
                blocks: vec![Block::Paragraph {
                    parts: vec![Inline::FileLink {
                        path: "a.rs".into(),
                        parts: vec![text("a")],
                    }],
                }],
            }],
        },
        Block::Rule,
    ];
    assert_eq!(
        markdown_of(&blocks),
        "> a\n> \n> b\n\n| h1 | h2 |\n| --- | --- |\n| 1 | 2 |\n\n1. [x] [x](https://x)\n  [a](a.rs)\n\n---"
    );
}

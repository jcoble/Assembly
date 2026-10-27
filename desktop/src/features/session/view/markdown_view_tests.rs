use super::{LinkTarget, Marks, Runs};
use crate::features::session::rows::markdown::Inline;

fn text(value: &str) -> Inline {
    Inline::Text {
        value: value.into(),
    }
}

#[test]
fn a_paragraph_is_one_string_with_its_marked_ranges() {
    let parts = vec![
        text("plain "),
        Inline::Strong {
            parts: vec![
                text("bold "),
                Inline::Emphasis {
                    parts: vec![text("both")],
                },
            ],
        },
        Inline::Code { value: "x".into() },
    ];
    let runs = Runs::of(&parts);
    assert_eq!(runs.text, "plain bold bothx");
    let bold = Marks {
        bold: true,
        ..Marks::default()
    };
    assert_eq!(
        runs.marked,
        vec![
            (6..11, bold),
            (
                11..15,
                Marks {
                    italic: true,
                    ..bold
                }
            ),
            (
                15..16,
                Marks {
                    code: true,
                    ..Marks::default()
                }
            ),
        ]
    );
    assert!(runs.links.is_empty());
}

#[test]
fn links_and_file_links_keep_their_range_and_target() {
    let parts = vec![
        text("see "),
        Inline::Link {
            href: "https://example.com".into(),
            parts: vec![
                text("the "),
                Inline::Strong {
                    parts: vec![text("docs")],
                },
            ],
        },
        text(" and "),
        Inline::FileLink {
            path: "src/main.rs".into(),
            parts: vec![text("main.rs")],
        },
    ];
    let runs = Runs::of(&parts);
    assert_eq!(runs.text, "see the docs and main.rs");
    assert_eq!(
        runs.links,
        vec![
            (4..12, LinkTarget::Url("https://example.com".into())),
            (17..24, LinkTarget::File("src/main.rs".into())),
        ]
    );
    let link = Marks {
        link: true,
        ..Marks::default()
    };
    assert!(runs.marked.contains(&(4..8, link)));
    assert!(runs.marked.contains(&(8..12, Marks { bold: true, ..link })));
}

#[test]
fn an_empty_link_is_not_clickable() {
    let parts = vec![Inline::Link {
        href: "https://example.com".into(),
        parts: vec![],
    }];
    assert_eq!(Runs::of(&parts), Runs::default());
}

use super::{LinkTarget, Marks, Runs};
use crate::core::ui::markdown::payload::Inline;

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

#[test]
fn file_links_require_an_actual_session_context_while_external_urls_do_not() {
    use crate::core::intents::{open_file::OpenFile, open_url::OpenUrl};
    let file = LinkTarget::File("src/main.rs".into());
    assert!(file.action(None, None).is_none());
    let action = file.action(Some("actual-session"), None).unwrap();
    assert!(action.partial_eq(&OpenFile {
        session_id: "actual-session".into(),
        path: "src/main.rs".into(),
        line: None,
        column: None,
        pinned: true,
        expected_source: None,
        outside: None,
        whole: None,
    }));
    let url = LinkTarget::Url("https://example.com/review".into());
    assert!(url.action(None, None).unwrap().partial_eq(&OpenUrl {
        url: "https://example.com/review".into()
    }));
}

#[test]
fn a_file_link_keeps_its_rendered_source_even_when_the_same_session_id_has_another_owner() {
    use crate::core::{intents::open_file::OpenFile, socket::row_source::ExpectedRowSource};
    let source = ExpectedRowSource {
        machine: "owner-a".into(),
        generation: 17,
        route_machine: Some("captured-route".into()),
    };
    let replacement = ExpectedRowSource {
        machine: "owner-b".into(),
        generation: 24,
        route_machine: Some("replacement-route".into()),
    };
    let target = LinkTarget::File("src/main.rs".into());
    let action = target.action(Some("same-session"), Some(&source)).unwrap();
    let expected = OpenFile {
        session_id: "same-session".into(),
        path: "src/main.rs".into(),
        line: None,
        column: None,
        pinned: true,
        expected_source: Some(source),
        outside: None,
        whole: None,
    };
    assert!(action.partial_eq(&expected));
    assert!(!action.partial_eq(&OpenFile {
        expected_source: Some(replacement),
        ..expected
    }));
    assert!(target.action(None, None).is_none());
}

#[test]
fn an_absolute_file_link_with_no_session_still_opens() {
    use crate::core::intents::open_file::OpenFile;
    for path in ["/etc/hosts", "~/notes/plan.md"] {
        let target = LinkTarget::File(path.into());
        // A session pane with no session selected renders its replies with an empty session.
        assert!(
            target.opens(Some("")),
            "{path} is clickable with no session"
        );
        let action = target
            .action(Some(""), None)
            .expect("an absolute link opens");
        // An empty session sends it to the one resolver, which opens it by machine and path.
        assert!(action.partial_eq(&OpenFile {
            session_id: String::new(),
            path: path.into(),
            line: None,
            column: None,
            pinned: true,
            expected_source: None,
            outside: None,
            whole: None,
        }));
    }
}

#[test]
fn a_relative_file_link_with_no_session_is_still_dropped() {
    let target = LinkTarget::File("src/main.rs".into());
    assert!(!target.opens(Some("")));
    assert!(target.action(Some(""), None).is_none());
    assert!(target.opens(Some("actual-session")));
}

#[test]
fn content_from_outside_a_session_has_no_file_links() {
    // Pull request bodies and comments: GitHub's site-relative links would look clickable and always fail.
    for path in [
        "/jcoble/Assembly/pull/12",
        "/docs/setup.md",
        "~/notes.md",
        "src/main.rs",
    ] {
        let target = LinkTarget::File(path.into());
        assert!(!target.opens(None), "{path}");
        assert!(target.action(None, None).is_none(), "{path}");
    }
    assert!(LinkTarget::Url("https://example.com".into()).opens(None));
}

#[test]
fn a_double_slash_link_is_never_a_file() {
    let target = LinkTarget::File("//host/share/a.txt".into());
    for session in [Some("actual-session"), Some(""), None] {
        assert!(!target.opens(session), "{session:?}");
        assert!(target.action(session, None).is_none(), "{session:?}");
    }
}

// Feature 28 parity item 4: a file link ending in `:N` or `#L?N` opens at that line, as the old
// `splitConversationFileReference` did, and one ending in `:N:M` at that line and column, as compilers print it (note 2
// ruling 1); anything else after the path stays part of it. A link whose suffix was read keeps its text as written, so
// a file whose real name ends in the suffix still opens.
#[test]
fn a_file_link_s_line_suffix_opens_at_that_line() {
    use crate::core::intents::open_file::OpenFile;
    let at = |link: &str, path: &str, line: Option<u32>, column: Option<u32>| OpenFile {
        session_id: "s".into(),
        path: path.into(),
        line,
        column,
        pinned: true,
        expected_source: None,
        outside: None,
        whole: line.map(|_| link.to_string()),
    };
    for (link, path, line, column) in [
        ("a.rs:12:4", "a.rs", Some(12), Some(4)),
        ("/work/a.rs:3:1", "/work/a.rs", Some(3), Some(1)),
        ("a.rs:12:0", "a.rs:12:0", None, None),
        ("a.rs:x:4", "a.rs:x", Some(4), None),
    ] {
        let action = LinkTarget::File(link.into()).action(Some("s"), None);
        let action = action.unwrap_or_else(|| panic!("{link} opens"));
        assert!(action.partial_eq(&at(link, path, line, column)), "{link}");
    }
    for (link, path, line) in [
        ("src/a.ts:71", "src/a.ts", Some(71)),
        ("a.rs#L12", "a.rs", Some(12)),
        ("a.rs#l12", "a.rs", Some(12)),
        ("a.rs#12", "a.rs", Some(12)),
        ("/work/a.rs:3", "/work/a.rs", Some(3)),
        ("~/notes.md#L9", "~/notes.md", Some(9)),
        ("a.rs:0", "a.rs:0", None),
        ("a.rs:012", "a.rs:012", None),
        ("a.rs:", "a.rs:", None),
        ("a.rs:x", "a.rs:x", None),
        ("a.rs#L", "a.rs#L", None),
        ("a.rs:99999999999", "a.rs:99999999999", None),
        ("src/main.rs", "src/main.rs", None),
    ] {
        let action = LinkTarget::File(link.into()).action(Some("s"), None);
        let action = action.unwrap_or_else(|| panic!("{link} opens"));
        assert!(action.partial_eq(&at(link, path, line, None)), "{link}");
    }
}

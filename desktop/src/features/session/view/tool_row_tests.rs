use serde_json::json;

use super::{
    DIFF_SHOWN, OUTPUT_TAIL, command_of, diff_lines, failed_run, line_summary, line_words,
    output_tail, short_path,
};
use crate::features::session::model::fold::Fold;
use crate::features::session::rows::tool::ToolPayload;

fn numbered(count: usize) -> String {
    (1..=count).map(|n| format!("{n}\n")).collect()
}

// Pins: g0-session#13
#[test]
fn a_long_diff_shows_its_first_lines_until_shown_in_full() {
    let short = numbered(20);
    assert_eq!(diff_lines(&short, false), (short.lines().collect(), 0));
    let long = numbered(21);
    let (shown, hidden) = diff_lines(&long, false);
    assert_eq!((shown.len(), hidden), (DIFF_SHOWN, 21 - DIFF_SHOWN));
    assert_eq!(shown[0], "1");
    assert_eq!(diff_lines(&long, true).0.len(), 21);
}

// Pins: g0-session#13
#[test]
fn an_open_tool_shows_the_end_of_its_output() {
    let long = numbered(OUTPUT_TAIL + 5);
    let tail = output_tail(&long);
    assert_eq!(tail.lines().count(), OUTPUT_TAIL);
    assert!(tail.starts_with("6\n"));
    assert!(tail.ends_with(&format!("{}\n", OUTPUT_TAIL + 5)));
    assert_eq!(output_tail("one\ntwo"), "one\ntwo");
}

// Pins: g0-session#13
#[test]
fn an_open_tool_shows_its_command_not_its_arguments_json() {
    let input = r#"{"command":"hostname","description":"Shows the host"}"#;
    assert_eq!(command_of(input), Some("hostname".to_string()));
    assert_eq!(command_of(r#"{"file_path":"/a.rs"}"#), None);
    assert_eq!(command_of("not json"), None);
}

// Pins: g0-session#13
#[test]
fn a_codex_shell_call_shows_its_argv_as_one_command() {
    let argv = r#"{"command":["bash","-lc","cargo test"]}"#;
    assert_eq!(command_of(argv), Some("bash -lc cargo test".to_string()));
    let cmd = r#"{"cmd":["git","status"]}"#;
    assert_eq!(command_of(cmd), Some("git status".to_string()));
    assert_eq!(
        command_of(r#"{"cmd":"ls -la"}"#),
        Some("ls -la".to_string())
    );
    assert_eq!(command_of(r#"{"command":["bash",1]}"#), None);
}

// Pins: g0-session#13
#[test]
fn a_tool_line_shows_the_last_two_parts_of_its_path() {
    let long = "/home/me/.local/share/assembly/sessions/s-1/notes/plan.md";
    assert_eq!(short_path(long), "notes/plan.md");
    assert_eq!(short_path(r"C:\work\desktop\main.rs"), "desktop/main.rs");
    assert_eq!(short_path("/a.rs"), "a.rs");
    assert_eq!(short_path("notes/plan.md"), "notes/plan.md");
}

// Pins: g0-session#13
#[test]
fn an_edit_titled_by_its_kind_shows_its_file_not_the_tools_own_verb() {
    let edit = |path: Option<&str>| {
        let tool = json!({ "name": "Editing files", "state": "completed", "path": path, "added": 3,
            "hasDetail": true, "kind": "edit" });
        serde_json::from_value::<ToolPayload>(tool).unwrap()
    };
    let words = |tool: &ToolPayload| {
        Fold::tool_words(
            &tool.name,
            tool.kind.as_deref(),
            line_summary(tool).as_deref(),
        )
    };
    assert_eq!(
        words(&edit(Some("/home/me/scratch/src/calc.py"))),
        ("Edited".to_string(), Some("src/calc.py".to_string()))
    );
    assert_eq!(words(&edit(None)), ("Edited".to_string(), None));
}

// Pins: g0-session#13
#[test]
fn a_failed_run_reads_failed_to_run_and_opens() {
    let call = |kind: &str, state: &str| {
        let tool = json!({ "name": "Bash", "state": state, "summary": "cat missing.txt", "hasDetail": true,
            "kind": kind });
        serde_json::from_value::<ToolPayload>(tool).unwrap()
    };
    let failed = call("execute", "failed");
    assert_eq!(
        line_words(&failed, false),
        (
            "Failed to run".to_string(),
            Some("cat missing.txt".to_string())
        )
    );
    assert!(failed_run(&failed, false));
    // A denied run didn't run; a run that worked, or a failed edit, keeps its own word and stays shut.
    assert_eq!(line_words(&failed, true).0, "Ran");
    assert!(!failed_run(&failed, true));
    assert_eq!(line_words(&call("execute", "completed"), false).0, "Ran");
    assert!(!failed_run(&call("execute", "completed"), false));
    assert_eq!(line_words(&call("edit", "failed"), false).0, "Edited");
    assert!(!failed_run(&call("edit", "failed"), false));
}

#[test]
fn cancelled_tool_payload_preserves_the_actual_terminal_state_and_existing_spellings() {
    use crate::features::session::rows::tool::ToolState;
    let payload = |state: &str| {
        serde_json::from_value::<ToolPayload>(json!({
        "name": "Bash", "state": state, "summary": "sleep 15", "hasDetail": true, "kind": "execute"
    })).unwrap()
    };
    let cancelled = payload("cancelled");
    assert_eq!(cancelled.state, ToolState::Cancelled);
    assert!(!failed_run(&cancelled, false));
    assert_eq!(
        line_words(&cancelled, false),
        ("Ran".to_string(), Some("sleep 15".to_string()))
    );
    assert_eq!(payload("started").state, ToolState::Started);
    assert_eq!(payload("completed").state, ToolState::Completed);
    assert_eq!(payload("failed").state, ToolState::Failed);
}

#[gpui::test]
fn cancelled_tool_line_is_neutral_with_and_without_activity_timing(cx: &mut gpui::TestAppContext) {
    use super::ToolRow;
    use crate::features::session::model::kept::Kept;
    use crate::features::session::view::step_row::StepState;
    cx.update(|cx| {
        gpui_kit::init(cx);
        for (state, denied, timed, expected) in [
            ("cancelled", false, false, StepState::Unfinished),
            ("cancelled", false, true, StepState::Unfinished),
            ("started", false, false, StepState::Unfinished),
            ("started", false, true, StepState::Running),
            ("completed", false, true, StepState::Done),
            ("failed", false, true, StepState::Failed),
            ("failed", true, true, StepState::Unfinished),
        ] {
            let row = ToolRow {
                key: "C".into(),
                tool: serde_json::from_value(
                    json!({"name":"Bash","state":state,"hasDetail":true,"kind":"execute"}),
                )
                .unwrap(),
                kept: Kept::default(),
                took_ms: timed.then_some(3000),
                denied,
                on_action: std::rc::Rc::new(|_, _, _| {}),
                session: None,
                expected_source: None,
            };
            assert_eq!(row.line(None, cx).state, expected);
        }
    });
}

/// A finished call of `kind` on `path`, with `summary` as the backend sent it, in `session`.
fn call_row(
    kind: &str,
    path: &str,
    summary: Option<&str>,
    session: Option<&str>,
) -> super::ToolRow {
    use crate::core::socket::row_source::ExpectedRowSource;
    super::ToolRow {
        key: "R".into(),
        tool: serde_json::from_value(json!({
            "name": "Tool", "state": "completed", "hasDetail": true, "kind": kind,
            "path": path, "summary": summary,
        }))
        .unwrap(),
        kept: crate::features::session::model::kept::Kept::default(),
        took_ms: None,
        denied: false,
        on_action: std::rc::Rc::new(|_, _, _| {}),
        session: session.map(Into::into),
        expected_source: Some(ExpectedRowSource {
            machine: "owner-a".into(),
            generation: 3,
            route_machine: Some("route-a".into()),
        }),
    }
}

/// A finished read of `path` whose summary is `summary`.
fn read_row(path: &str, summary: &str, session: Option<&str>) -> super::ToolRow {
    call_row("read", path, Some(summary), session)
}

fn opens(row: &super::ToolRow, cx: &gpui_kit::App) -> Option<Box<dyn gpui_kit::Action>> {
    row.line(None, cx).summary_action
}

fn opening(session: &str, path: &str) -> crate::core::intents::open_file::OpenFile {
    crate::core::intents::open_file::OpenFile {
        session_id: session.into(),
        path: path.into(),
        line: None,
        column: None,
        pinned: true,
        expected_source: read_row("/x", "/x", None).expected_source,
        outside: None,
    }
}

#[gpui::test]
fn a_tool_row_path_is_a_file_link_for_its_session(cx: &mut gpui::TestAppContext) {
    cx.update(|cx| {
        gpui_kit::init(cx);
        // An absolute path, and a relative one the backend joined onto the call's folder (its summary stays as sent),
        // on every file kind.
        for kind in ["read", "edit", "delete", "move"] {
            for summary in ["/work/src/a.rs", "src/a.rs"] {
                let row = call_row(kind, "/work/src/a.rs", Some(summary), Some("S"));
                let action = opens(&row, cx).expect("the path is a link");
                assert!(
                    action.partial_eq(&opening("S", "/work/src/a.rs")),
                    "{kind} {summary}"
                );
            }
        }
        // With no session an absolute path still goes to the resolver; a relative one has nowhere to open.
        let alone = opens(&read_row("/work/src/a.rs", "/work/src/a.rs", None), cx);
        assert!(
            alone
                .expect("absolute opens")
                .partial_eq(&opening("", "/work/src/a.rs"))
        );
        assert!(opens(&read_row("src/a.rs", "src/a.rs", None), cx).is_none());
        assert!(opens(&read_row("src/a.rs", "src/a.rs", Some("S")), cx).is_some());
        // A line that shows something other than the path isn't a link, nor one with no summary of its own.
        assert!(opens(&read_row("/work/src/a.rs", "reads the file", Some("S")), cx).is_none());
        assert!(opens(&call_row("read", "/work/src/a.rs", None, Some("S")), cx).is_none());
        assert!(opens(&read_row("/work/src/a.rs", "", Some("S")), cx).is_none());
        // `//host/...` is never a file path.
        assert!(opens(&read_row("//host/a.rs", "//host/a.rs", Some("S")), cx).is_none());
    });
}

#[gpui::test]
fn a_folder_or_a_search_pattern_is_not_a_file_link(cx: &mut gpui::TestAppContext) {
    cx.update(|cx| {
        gpui_kit::init(cx);
        // An LS of a folder, and a Grep whose pattern happens to end its path.
        let ls = call_row("search", "/repo/src", Some("/repo/src"), Some("S"));
        assert!(opens(&ls, cx).is_none());
        let grep = call_row("search", "/repo/foo", Some("foo"), Some("S"));
        assert!(opens(&grep, cx).is_none());
        assert_eq!(grep.line(None, cx).summary.as_deref(), Some("foo"));
        let other = call_row("other", "/repo/src", Some("/repo/src"), Some("S"));
        assert!(opens(&other, cx).is_none());
    });
}

#[gpui::test]
fn the_tool_row_still_shows_the_short_path(cx: &mut gpui::TestAppContext) {
    cx.update(|cx| {
        gpui_kit::init(cx);
        let summary = |row: super::ToolRow| row.line(None, cx).summary.map(|s| s.to_string());
        let full = "/work/desktop/src/core/ui/markdown/view.rs";
        assert_eq!(
            summary(read_row(full, full, Some("S"))).as_deref(),
            Some("markdown/view.rs")
        );
        // A relative path the backend joined onto the call's folder shows its last two parts too.
        let joined = read_row(full, "desktop/src/core/ui/markdown/view.rs", Some("S"));
        assert_eq!(summary(joined).as_deref(), Some("markdown/view.rs"));
        let bare = call_row("read", full, None, Some("S"));
        assert_eq!(summary(bare).as_deref(), Some("markdown/view.rs"));
    });
}

#[gpui::test]
fn clicking_the_tool_path_does_not_toggle_the_row(cx: &mut gpui::TestAppContext) {
    use std::cell::RefCell;
    use std::rc::Rc;

    use gpui_kit::{
        AppContext as _, Context, Entity, IntoElement, Modifiers, ParentElement as _, Render,
        Styled as _, Window,
    };

    use crate::core::intents::open_file::OpenFile;
    use crate::core::ui::expand_row::ExpandRow;

    struct Host(Entity<ExpandRow>);
    impl Render for Host {
        fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
            gpui_kit::component::v_flex()
                .size_full()
                .child(self.0.clone())
        }
    }

    let opened: Rc<RefCell<Vec<OpenFile>>> = Rc::default();
    let heard = opened.clone();
    cx.update(|cx| {
        gpui_kit::init(cx);
        cx.on_action(move |intent: &OpenFile, _| heard.borrow_mut().push(intent.clone()));
    });
    let row = read_row("/work/src/a.rs", "/work/src/a.rs", Some("S"));
    let (host, cx) = cx.add_window_view(move |_, cx| {
        let expand = cx.new(|_| {
            ExpandRow::new(
                false,
                move |grown, _, cx| row.line(Some(grown), cx).into_any_element(),
                |_, _| gpui_kit::div().into_any_element(),
            )
        });
        Host(expand)
    });
    cx.run_until_parked();
    let expand = cx.update(|_, cx| host.read(cx).0.clone());
    let at = |name: &'static str, cx: &mut gpui::VisualTestContext| {
        cx.update(|window, cx| window.draw(cx).clear(cx));
        cx.debug_bounds(name).expect("drawn")
    };
    let path = at("R:line:summary", cx);
    cx.simulate_click(path.center(), Modifiers::none());
    cx.run_until_parked();
    assert!(
        !cx.update(|_, cx| expand.read(cx).is_open()),
        "the row stays shut"
    );
    let opened_paths: Vec<_> = opened.borrow().iter().map(|o| o.path.clone()).collect();
    assert_eq!(opened_paths, vec!["/work/src/a.rs".to_string()]);
    // The rest of the line still opens it.
    let line = at("R:line", cx);
    cx.simulate_click(
        gpui_kit::point(line.left() + gpui_kit::px(4.), line.center().y),
        Modifiers::none(),
    );
    cx.run_until_parked();
    assert!(cx.update(|_, cx| expand.read(cx).is_open()));
    assert_eq!(opened.borrow().len(), 1);
}

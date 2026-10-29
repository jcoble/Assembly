use super::{DIFF_SHOWN, OUTPUT_TAIL, command_of, diff_lines, output_tail, short_path};

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

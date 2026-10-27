use super::{DIFF_SHOWN, OUTPUT_TAIL, diff_lines, output_tail};

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

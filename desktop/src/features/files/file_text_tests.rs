//! A file's text joins its parts into lines, asks for the next part only near the end, and starts over when the file
//! changes between parts.

use super::{FileText, KEEPS_CHANGING, NEAR_END};
use crate::features::files::entry::FileChunk;

fn chunk(content: &str, revision: &str, next: Option<u64>) -> Result<FileChunk, String> {
    Ok(FileChunk {
        content: content.into(),
        size: 100,
        revision: revision.into(),
        line_count: 3,
        binary: false,
        next_offset: next,
    })
}

// Pins: files#11
#[test]
fn a_whole_file_is_its_lines() {
    let mut text = FileText::default();
    assert_eq!(text.start(), 0);
    assert!(text.is_loading());
    assert_eq!(text.loaded(0, chunk("a\nb\nc", "r1", None)), None);
    assert_eq!(text.lines, ["a", "b", "c"]);
    assert!(!text.is_loading());
}

// Pins: files#12
#[test]
fn parts_join_and_a_line_split_between_two_parts_is_one_line() {
    let mut text = FileText::default();
    text.start();
    text.loaded(0, chunk("a\nbe", "r1", Some(4)));
    assert_eq!(text.more(0), Some(4));
    text.loaded(4, chunk("gin\nc\n", "r1", None));
    assert_eq!(text.lines, ["a", "begin", "c"]);
}

// Pins: files#12
#[test]
fn the_next_part_is_asked_for_once_and_only_near_the_end() {
    let mut text = FileText::default();
    text.start();
    let many: String = (0..NEAR_END * 2).map(|i| format!("{i}\n")).collect();
    text.loaded(0, chunk(&many, "r1", Some(500)));
    assert_eq!(text.more(0), None, "far from the end");
    assert_eq!(text.more(NEAR_END * 2 - 10), Some(500));
    assert_eq!(text.more(NEAR_END * 2 - 10), None, "already loading");
}

// Pins: files#14
#[test]
fn a_part_from_a_changed_file_starts_over_from_the_top() {
    let mut text = FileText::default();
    text.start();
    text.loaded(0, chunk("a\n", "r1", Some(2)));
    text.more(1);
    assert_eq!(text.loaded(2, chunk("b\n", "r2", Some(4))), Some(0));
    assert!(text.lines.is_empty());
    text.loaded(0, chunk("new\n", "r2", None));
    assert_eq!(text.lines, ["new"]);
}

// Pins: files#15
#[test]
fn an_answer_for_a_part_nobody_asked_for_is_dropped() {
    let mut text = FileText::default();
    text.start();
    text.loaded(0, chunk("a\n", "r1", Some(2)));
    assert_eq!(text.loaded(2, chunk("b\n", "r1", None)), None);
    assert_eq!(text.lines, ["a"]);
}

// Pins: files#12, files#13
#[test]
fn a_failure_or_a_binary_file_shows_no_lines() {
    let mut text = FileText::default();
    text.start();
    text.loaded(
        0,
        Err("that file is 9 MiB, more than the 8 MiB this view shows".into()),
    );
    assert!(text.lines.is_empty());
    assert_eq!(
        text.error.as_deref(),
        Some("that file is 9 MiB, more than the 8 MiB this view shows")
    );
    text.start();
    assert_eq!(text.error, None);
    let mut binary = chunk("", "r1", None).unwrap();
    binary.binary = true;
    text.loaded(0, Ok(binary));
    assert!(text.binary && text.lines.is_empty());
}

// Pins: files#14
#[test]
fn a_later_part_that_fails_starts_over_once_then_says_why() {
    let mut text = FileText::default();
    text.start();
    text.loaded(0, chunk("a\n", "r1", Some(2)));
    text.more(1);
    assert_eq!(
        text.loaded(2, Err("the offset is outside the file".into())),
        Some(0),
        "the file shrank, so it starts over"
    );
    text.loaded(0, chunk("a\n", "r2", Some(2)));
    text.more(1);
    assert_eq!(text.loaded(2, Err("still failing".into())), None);
    assert_eq!(text.error.as_deref(), Some("still failing"));
}

// Pins: files#11
#[test]
fn a_crlf_file_shows_its_lines_without_the_carriage_return_and_knows_its_widest_line() {
    let mut text = FileText::default();
    text.start();
    text.loaded(0, chunk("a\r\nlongest\r\nbb\r\n", "r1", None));
    assert_eq!(text.lines, ["a", "longest", "bb"]);
    assert_eq!(text.widest, 1);
}

// Pins: files#12
#[test]
fn after_a_failure_that_is_shown_nothing_more_is_asked_for() {
    let mut text = FileText::default();
    text.start();
    text.loaded(0, chunk("a\n", "r1", Some(2)));
    text.failed("The backend isn't running.".into());
    assert_eq!(text.more(0), None);
    assert!(!text.is_loading());
}

// Pins: files#11
#[test]
fn a_crlf_split_between_parts_is_dropped_but_a_lone_carriage_return_at_a_part_end_is_kept() {
    let mut text = FileText::default();
    text.start();
    text.loaded(0, chunk("a\r", "r1", Some(2)));
    text.more(0);
    text.loaded(2, chunk("\nb\r", "r1", Some(5)));
    assert_eq!(text.lines, ["a", "b\r"]);
    text.more(0);
    text.loaded(5, chunk("c", "r1", None));
    assert_eq!(text.lines, ["a", "b\rc"]);
}

// Pins: files#14
#[test]
fn a_file_that_changes_twice_while_read_stops_and_says_so() {
    let mut text = FileText::default();
    text.start();
    text.loaded(0, chunk("a\n", "r1", Some(2)));
    text.more(1);
    assert_eq!(text.loaded(2, chunk("b\n", "r2", Some(4))), Some(0));
    text.loaded(0, chunk("a\n", "r2", Some(2)));
    text.more(1);
    assert_eq!(text.loaded(2, chunk("b\n", "r3", Some(4))), None);
    assert_eq!(text.error.as_deref(), Some(KEEPS_CHANGING));
    assert_eq!(text.more(0), None, "nothing more is read");
}

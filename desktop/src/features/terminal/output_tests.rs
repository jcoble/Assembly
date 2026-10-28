use base64::Engine as _;
use base64::engine::general_purpose::STANDARD;

use super::{Output, OutputPiece, OutputRow, REPLAY};

fn row(prev_seq: i64, data: &str) -> OutputRow {
    OutputRow {
        seq: prev_seq + data.len() as i64,
        prev_seq,
        data: Some(STANDARD.encode(data)),
        exit: None,
    }
}

fn piece(offset: i64, data: &str) -> OutputPiece {
    OutputPiece {
        offset,
        data: STANDARD.encode(data),
    }
}

// Pins: terminal#3
#[test]
fn live_rows_pass_through_byte_for_byte_and_a_repeat_is_dropped() {
    let mut output = Output::default();
    assert_eq!(output.row(row(0, "ab")), b"ab");
    assert_eq!(output.row(row(2, "\u{e9}")), "\u{e9}".as_bytes());
    assert_eq!(output.row(row(0, "ab")), b"");
    assert_eq!(
        output.row(row(3, "xyz")),
        b"yz",
        "only what's past the last byte"
    );
}

// Pins: terminal#5
#[test]
fn a_new_view_reads_the_last_4_mb_then_the_rows_held_meanwhile_with_nothing_repeated() {
    let mut output = Output::default();
    let end = REPLAY + 10;
    assert_eq!(
        output.jump(end),
        Some(10),
        "no earlier than 4 MB before the end"
    );
    assert_eq!(
        output.row(row(end, "live")),
        b"",
        "held while the replay reads"
    );
    let (bytes, _, next) = output.page(piece(10, &"x".repeat(5)));
    assert_eq!((bytes.len(), next), (5, Some(15)));
    // The page ran on past the jump (output arrived since): only up to the jump comes from the replay.
    let rest = "y".repeat((REPLAY - 5) as usize) + "live";
    let (bytes, held, next) = output.page(piece(15, &rest));
    assert_eq!(next, None);
    assert_eq!(bytes.len() as i64, REPLAY - 5);
    assert_eq!(held, b"live", "the held rows come apart, as new output");
}

// Pins: terminal#5
#[test]
fn a_jump_the_screen_already_has_reads_nothing() {
    let mut output = Output::default();
    output.row(row(0, "hello"));
    assert_eq!(output.jump(5), None);
    assert_eq!(output.row(row(5, "!")), b"!");
}

// Pins: terminal#6
#[test]
fn trimmed_output_carries_on_from_what_the_ring_still_has() {
    let mut output = Output::default();
    assert_eq!(output.jump(100), Some(0));
    let (bytes, _, next) = output.page(piece(60, &"z".repeat(40)));
    assert_eq!((bytes.len(), next), (40, None));
}

// Pins: terminal#7
#[test]
fn the_exit_comes_after_the_last_output_and_moves_no_offset() {
    let mut output = Output::default();
    output.row(row(0, "bye"));
    let exit = OutputRow {
        seq: 4,
        prev_seq: 3,
        data: None,
        exit: Some(130),
    };
    assert_eq!(output.row(exit), b"");
    assert_eq!(output.exit(), Some(130));
}

// Pins: terminal#5
#[test]
fn an_empty_page_ends_the_replay() {
    let mut output = Output::default();
    output.jump(50);
    output.row(row(50, "x"));
    let (bytes, held, next) = output.page(piece(0, ""));
    assert_eq!((bytes, held, next), (Vec::new(), b"x".to_vec(), None));
}

// Pins: terminal#5
#[test]
fn a_page_past_the_replays_end_keeps_the_held_rows() {
    let mut output = Output::default();
    output.jump(10);
    output.row(row(10, "abc"));
    output.row(row(13, "de"));
    let (bytes, held, next) = output.page(piece(14, "eXYZ"));
    assert_eq!((bytes, held, next), (Vec::new(), b"abcde".to_vec(), None));
}

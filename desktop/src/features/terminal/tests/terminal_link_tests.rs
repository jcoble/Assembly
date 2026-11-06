//! What the link makes of the backend's rows and failure codes.

use serde_json::{Value, json};

use super::{Availability, Output, TerminalLink, connection_down, source_changed};
use crate::core::socket::call_failure::CallFailure;
use crate::core::socket::error_code::ErrorCode;
use crate::core::socket::update::Update;

fn link(end: i64) -> TerminalLink {
    TerminalLink {
        id: "term-1".into(),
        end,
    }
}

fn row(item: Value) -> Update {
    Update::Change {
        key: "term-1".into(),
        item,
    }
}

fn failure(code: ErrorCode) -> CallFailure {
    CallFailure {
        code,
        message: "why".into(),
    }
}

#[test]
fn availability_rows_parse_for_all_three_values() {
    for (wire, value) in [
        ("connection_lost", Availability::ConnectionLost),
        ("terminal_missing", Availability::TerminalMissing),
        ("source_changed", Availability::SourceChanged),
    ] {
        // An availability row has no offsets (OutputRow.cs).
        let step = link(5).step(&row(json!({"unavailable": wire})));
        assert_eq!(step, Ok(Some(Output::Unavailable(value))), "{wire}");
    }
}

#[test]
fn an_empty_boundary_row_gives_caught() {
    let mut link = link(5);
    let step = link.step(&row(json!({"seq": 5, "prevSeq": 5, "data": ""})));
    assert_eq!(step, Ok(Some(Output::Caught)));
    assert_eq!(link.end, 5);
}

#[test]
fn a_duplicate_row_gives_caught() {
    let mut link = link(5);
    let step = link.step(&row(json!({"seq": 3, "prevSeq": 0, "data": "YWJj"})));
    assert_eq!(step, Ok(Some(Output::Caught)));
    assert_eq!(link.end, 5);
}

#[test]
fn bytes_still_skip_what_was_handed_on() {
    let mut link = link(3);
    let step = link.step(&row(json!({"seq": 6, "prevSeq": 0, "data": "YWJjZGVm"})));
    assert_eq!(step, Ok(Some(Output::Bytes(b"def".to_vec()))));
    assert_eq!(link.end, 6);
}

#[test]
fn from_wire_maps_the_two_new_codes() {
    assert_eq!(
        ErrorCode::from_wire("connection_lost", None),
        ErrorCode::ConnectionLost
    );
    assert_eq!(
        ErrorCode::from_wire("terminal_missing", None),
        ErrorCode::TerminalMissing
    );
}

#[test]
fn connection_down_and_source_changed_classify() {
    let unreachable = ErrorCode::Unreachable {
        machine: "box".into(),
    };
    assert!(connection_down(&failure(ErrorCode::ConnectionLost)));
    assert!(connection_down(&failure(unreachable)));
    for other in [
        ErrorCode::Failed,
        ErrorCode::TerminalMissing,
        ErrorCode::from_wire("source_changed", None),
    ] {
        assert!(!connection_down(&failure(other.clone())), "{other:?}");
    }
    assert!(source_changed(&failure(ErrorCode::from_wire(
        "source_changed",
        None
    ))));
    for other in [
        ErrorCode::ConnectionLost,
        ErrorCode::Failed,
        ErrorCode::from_wire("checkout_unavailable", None),
    ] {
        assert!(!source_changed(&failure(other.clone())), "{other:?}");
    }
}

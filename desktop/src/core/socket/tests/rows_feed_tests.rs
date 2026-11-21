//! `RowsFeed`'s reading of a subscription's `state` frames.

use super::RowsFeed;

// TSK-1692: a saved copy dropped and downloaded again restarts its rows from seq 0. The window must reload, not skip.
#[test]
fn state_zero_after_rows_is_a_jump() {
    let mut feed = RowsFeed {
        session_id: "s".into(),
        last_seq: Some(300),
        expected_source: None,
    };
    assert_eq!(feed.state(0), Some(0));
    assert_eq!(feed.last_seq, Some(0));
    assert_eq!(feed.state(0), None);
}

//! `RowsFeed` is the client thread's record of one `rows` subscription: its session and the last seq it passed on
//! (the rule in rows/store.ts `liveStep`, now on the socket). A row at or below it is skipped (already sent); a row
//! whose `prevSeq` isn't it means one was missed, so the subscription is sent again (`unsub`, then `sub`) with
//! `after` = the last seq. A reconnect subscribes again the same way. A `state` whose `seq` isn't the `after` sent is
//! a `RowUpdate::Jump`.

pub(super) struct RowsFeed {
    pub session_id: String,
    pub last_seq: Option<i64>,
}

/// What to do with a row that arrived.
pub(super) enum RowStep {
    Pass,
    Skip,
    Gap,
}

impl RowsFeed {
    pub fn step(&mut self, _seq: i64, _prev_seq: Option<i64>) -> RowStep {
        todo!()
    }

    /// A `state` arrived with `seq`: `None` when it's the `after` sent, else the jump to pass on.
    pub fn state(&mut self, _seq: i64) -> Option<i64> {
        todo!()
    }
}

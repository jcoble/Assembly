//! `RowUpdate` is what a rows subscription gets: each row after the last one, in order, none missed and none twice,
//! or a jump. The backend jumps when `after` is too far behind (for `rows`, over 500 rows or 8 MB) or past the end:
//! its `state` has a `seq` other than the `after` sent, and the subscription carries on from there. `R` is the row as
//! the topic sends it: a session's `Row` on `rows`, a terminal's output on `terminal`.

use futures_channel::mpsc::UnboundedSender;
use serde::de::DeserializeOwned;
use serde_json::Value;

use super::row::Row;

#[derive(Debug)]
pub enum RowUpdate<R = Box<Row>> {
    Row(R),
    /// The rows before `seq` weren't sent: drop what's held and reload them (for `rows`, with `rows/page`,
    /// `before` = `seq + 1`).
    Jump {
        seq: i64,
    },
}

/// What the client thread hands a rows subscription's updates to, with each row still JSON.
pub(super) type RowsSink = Box<dyn FnMut(RowUpdate<Value>) + Send>;

/// A sink that reads each row into `R` and sends it on; a row that doesn't read is logged and dropped.
pub(super) fn rows_sink<R: DeserializeOwned + Send + 'static>(
    updates: UnboundedSender<RowUpdate<R>>,
) -> RowsSink {
    Box::new(move |update| {
        let update = match update {
            RowUpdate::Row(row) => match serde_json::from_value(row) {
                Ok(row) => RowUpdate::Row(row),
                Err(error) => return eprintln!("socket: a row didn't read: {error}"),
            },
            RowUpdate::Jump { seq } => RowUpdate::Jump { seq },
        };
        let _ = updates.unbounded_send(update);
    })
}

//! `RowUpdate` is what a `rows` subscription gets: each row after the last one, in order, none missed and none twice,
//! or a jump. The backend jumps when `after` is too far behind (over 500 rows or 8 MB) or past the end: its `state`
//! has a `seq` other than the `after` sent, and the subscription carries on from there.

use super::row::Row;

#[derive(Debug)]
pub enum RowUpdate {
    Row(Box<Row>),
    /// The rows before `seq` weren't sent: drop what's held and reload with `rows/page`, `before` = `seq + 1`.
    Jump {
        seq: i64,
    },
}

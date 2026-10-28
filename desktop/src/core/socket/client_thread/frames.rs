//! How the client thread applies each frame the backend sends: answers settle their calls, a subscription's error
//! drops it, `state` and `change` go to their topic, and rows pass through their feed's seq check, a gap
//! re-subscribing from the last seq.

use std::sync::atomic::Ordering;

use serde_json::Value;

use super::ClientThread;
use crate::core::socket::call_failure::CallFailure;
use crate::core::socket::error_code::ErrorCode;
use crate::core::socket::in_frame::InFrame;
use crate::core::socket::out_frame::OutFrame;
use crate::core::socket::outcome::Outcome;
use crate::core::socket::row_update::RowUpdate;
use crate::core::socket::rows_feed::RowStep;
use crate::core::socket::update::Update;

impl ClientThread {
    pub(super) fn frame(&mut self, frame: InFrame) {
        match frame {
            InFrame::Welcome { .. } => {}
            InFrame::Result { id, result } => self.settle(id, Outcome::Ok(result)),
            InFrame::Error {
                id,
                code,
                message,
                machine,
            } => {
                if let Some(topic) = self.topics.remove(&id) {
                    eprintln!("socket: {} failed: {message}", topic.topic);
                } else if let Some(rows) = self.rows.remove(&id) {
                    eprintln!("socket: {} failed: {message}", rows.feed.topic);
                } else if code == "cancelled" {
                    self.settle(id, Outcome::Aborted);
                } else {
                    let code = ErrorCode::from_wire(&code, machine);
                    self.settle(id, Outcome::Failed(CallFailure { code, message }));
                }
            }
            InFrame::State { sub, seq, items } => {
                if let Some(topic) = self.topics.get(&sub) {
                    let items = items
                        .into_iter()
                        .map(|entry| (entry.key, entry.item))
                        .collect();
                    let _ = topic.updates.unbounded_send(Update::State(items));
                } else if let (Some(rows), Some(seq)) = (self.rows.get_mut(&sub), seq)
                    && let Some(seq) = rows.feed.state(seq)
                {
                    (rows.updates)(RowUpdate::Jump { seq });
                }
            }
            InFrame::Change { sub, key, item } => {
                if let Some(topic) = self.topics.get(&sub) {
                    let _ = topic.updates.unbounded_send(Update::Change { key, item });
                }
            }
            InFrame::Rows { sub, row } => {
                let Some(rows) = self.rows.get_mut(&sub) else {
                    return;
                };
                let Some(seq) = row.get("seq").and_then(Value::as_i64) else {
                    return eprintln!("socket: a {} row has no seq", rows.feed.topic);
                };
                let prev_seq = row.get("prevSeq").and_then(Value::as_i64);
                match rows.feed.step(seq, prev_seq) {
                    RowStep::Pass => (rows.updates)(RowUpdate::Row(row)),
                    RowStep::Skip => {}
                    RowStep::Gap => self.resubscribe_rows(sub),
                }
            }
        }
    }

    /// A gap: the old subscription goes, and a new one, under a fresh id so the old one's frames still on the way are
    /// ignored, asks for the rows after the last seq.
    fn resubscribe_rows(&mut self, wire: u64) {
        let Some(rows) = self.rows.remove(&wire) else {
            return;
        };
        self.send(&OutFrame::Unsub { id: wire });
        let id = self.ids.fetch_add(1, Ordering::Relaxed) + 1;
        self.rows.insert(id, rows);
        self.send_rows(id);
    }
}

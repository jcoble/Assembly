//! How the client thread applies each frame the backend sends: answers settle their calls, a subscription's error
//! drops it, `state` and `change` go to their topic, and rows pass through their feed's seq check, a gap
//! re-subscribing from the last seq. A row on a keyed topic (`terminal`) goes to it as a change of its key.

use std::sync::atomic::Ordering;

use super::ClientThread;
use crate::core::socket::call_failure::CallFailure;
use crate::core::socket::error_code::ErrorCode;
use crate::core::socket::in_frame::InFrame;
use crate::core::socket::out_frame::OutFrame;
use crate::core::socket::outcome::Outcome;
use crate::core::socket::row::Row;
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
                } else if self.rows.remove(&id).is_some() {
                    eprintln!("socket: rows failed: {message}");
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
                    let _ = rows.updates.unbounded_send(RowUpdate::Jump { seq });
                }
            }
            InFrame::Change { sub, key, item } => {
                if let Some(topic) = self.topics.get(&sub) {
                    let _ = topic.updates.unbounded_send(Update::Change { key, item });
                }
            }
            InFrame::Rows { sub, row } => {
                if let Some(topic) = self.topics.get(&sub) {
                    let key = topic.key.clone().unwrap_or_default();
                    let _ = topic
                        .updates
                        .unbounded_send(Update::Change { key, item: row });
                    return;
                }
                let Some(rows) = self.rows.get_mut(&sub) else {
                    return;
                };
                let row: Row = match serde_json::from_value(row) {
                    Ok(row) => row,
                    // As a frame that doesn't read: reopening at once would get the same row again.
                    Err(error) => {
                        eprintln!("socket: a row this client doesn't read ({error})");
                        self.close();
                        self.disconnected();
                        return;
                    }
                };
                match rows.feed.step(row.seq, row.prev_seq) {
                    RowStep::Pass => {
                        let _ = rows.updates.unbounded_send(RowUpdate::Row(Box::new(row)));
                    }
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

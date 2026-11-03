//! Engine replay uses the global scanned cursor, including pages without matching events.
use super::ClientThread;
use crate::core::socket::engine_update::{CursorAt, EnginePage, EngineUpdate};
use crate::core::socket::out_frame::OutFrame;
use serde_json::Value;
use std::sync::atomic::Ordering;

impl ClientThread {
    pub(super) fn add_engine(
        &mut self,
        id: u64,
        topic: String,
        key: Option<String>,
        machine: Option<String>,
        expected_source:Option<crate::core::socket::row_source::ExpectedRowSource>,
        scoped: bool,
        updates: futures_channel::mpsc::UnboundedSender<EngineUpdate>,
    ) {
        let machine = machine.or_else(|| {
            self.connection
                .as_ref()
                .and_then(|connection| connection.engine_machine.clone())
        });
        self.engines.insert(
            id,
            crate::core::socket::engine_update::EngineFeed {
                handle: id,
                topic,
                key,
                scoped,
                expected_source,
                machine,
                updates,
                through: None,
                acknowledged: None,
                at: None,
                snapshot_cut: None,
                snapshot_items: Vec::new(),
            },
        );
        self.send_engine(id);
    }

    pub(super) fn engine_snapshot(
        &mut self,
        id: u64,
        machine: String,
        through: i64,
        items: Vec<Value>,
        more: bool,
        at: Option<CursorAt>,
    ) {
        let Some(feed) = self.engines.get_mut(&id) else {
            return;
        };
        if through < 0
            || feed
                .machine
                .as_ref()
                .is_some_and(|expected| expected != &machine)
        {
            self.reset_engine(id);
            return;
        }
        if feed.snapshot_cut.is_some_and(|cut| cut != through) {
            self.reset_engine(id);
            return;
        }
        feed.machine = Some(machine.clone());
        feed.snapshot_cut = Some(through);
        feed.snapshot_items.extend(items);
        if more {
            return;
        }
        let items = std::mem::take(&mut feed.snapshot_items);
        let update = if feed.scoped {
            EngineUpdate::Scoped(EnginePage {
                wire: id,
                machine,
                through,
                at: at.clone(),
                snapshot: true,
                items,
                events: Vec::new(),
            })
        } else {
            EngineUpdate::Snapshot(items)
        };
        if feed.updates.unbounded_send(update).is_err() {
            self.engines.remove(&id);
            self.send(&OutFrame::Unsub { id });
            return;
        }
        // messages pages are pushed consecutively; only mirror snapshots use next.
        if !more {
            feed.through = Some(through);
            feed.at = at;
            feed.snapshot_cut = None;
            if !feed.scoped {
                self.send(&OutFrame::Ack {
                    id,
                    stream: "engine",
                    after: through,
                });
            }
        }
    }

    pub(super) fn engine_events(
        &mut self,
        id: u64,
        machine: String,
        after: i64,
        through: i64,
        events: Vec<Value>,
        at: Option<CursorAt>,
    ) {
        let Some(feed) = self.engines.get_mut(&id) else {
            return;
        };
        if feed.machine.as_ref() != Some(&machine)
            || feed.snapshot_cut.is_some()
            || through < after
            || feed.through.is_none()
        {
            self.reset_engine(id);
            return;
        }
        let cursor = feed.through.unwrap();
        if through <= cursor {
            return;
        }
        if after != cursor
            || events
                .windows(2)
                .any(|pair| pair[0]["seq"].as_i64() >= pair[1]["seq"].as_i64())
            || events.iter().any(|event| {
                event["machine"].as_str() != Some(machine.as_str())
                    || event["seq"]
                        .as_i64()
                        .is_none_or(|seq| seq <= after || seq > through)
            })
        {
            self.reset_engine(id);
            return;
        }
        let update = if feed.scoped {
            EngineUpdate::Scoped(EnginePage {
                wire: id,
                machine,
                through,
                at: at.clone(),
                snapshot: false,
                items: Vec::new(),
                events,
            })
        } else {
            EngineUpdate::Events(events)
        };
        if feed.updates.unbounded_send(update).is_err() {
            self.engines.remove(&id);
            self.send(&OutFrame::Unsub { id });
            return;
        }
        feed.through = Some(through);
        feed.at = at;
        if !feed.scoped {
            self.send(&OutFrame::Ack {
                id,
                stream: "engine",
                after: through,
            });
        }
    }

    pub(super) fn reset_engine(&mut self, wire: u64) {
        let Some(mut feed) = self.engines.remove(&wire) else {
            return;
        };
        self.send(&OutFrame::Unsub { id: wire });
        feed.through = None;
        feed.acknowledged = None;
        feed.at = None;
        feed.snapshot_cut = None;
        feed.snapshot_items.clear();
        let id = self.ids.fetch_add(1, Ordering::Relaxed) + 1;
        self.engines.insert(id, feed);
        self.send_engine(id);
    }
}

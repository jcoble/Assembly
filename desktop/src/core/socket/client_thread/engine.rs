//! Engine subscription ownership and global-cursor validation. Reconnects and gaps take a fresh
//! wire ID; the feature's handle remains stable so queued old pages and ACKs cannot reach it.
use super::ClientThread;
use crate::core::socket::engine_subscription::{EnginePage, EngineUpdate};
use crate::core::socket::out_frame::OutFrame;
use std::sync::atomic::Ordering;

impl ClientThread {
    pub(super) fn send_engine(&mut self, id: u64) {
        let Some(engine) = self.engines.get(&id) else {
            return;
        };
        let (topic, machine) = (engine.topic.clone(), engine.machine.clone());
        self.send(&OutFrame::EngineSub {
            id,
            stream: "engine",
            topic: &topic,
            machine: &machine,
        });
    }
    pub(super) fn resubscribe_engine(&mut self, wire: u64) {
        let Some(mut engine) = self.engines.remove(&wire) else {
            return;
        };
        self.send(&OutFrame::Unsub { id: wire });
        let id = self.ids.fetch_add(1, Ordering::Relaxed) + 1;
        engine.through = None;
        engine.snapshot = false;
        engine.ack = None;
        self.engines.insert(id, engine);
        self.send_engine(id);
    }
    pub(super) fn ack_engine(&mut self, handle: u64, wire: u64, through: i64) {
        let Some(engine) = self.engines.get_mut(&wire) else {
            return;
        };
        if engine.handle != handle
            || engine.snapshot
            || engine.through.is_none_or(|seen| through > seen)
            || engine.ack.is_some_and(|ack| through < ack)
            || through < 0
        {
            return;
        }
        engine.ack = Some(through);
        self.send(&OutFrame::EngineAck {
            id: wire,
            stream: "engine",
            after: through,
        });
    }
    pub(super) fn engine_page(&mut self, id: u64, machine: String, page: EnginePage) {
        let Some(engine) = self.engines.get_mut(&id) else {
            return;
        };
        if engine.machine != machine {
            return;
        }
        let page = match page {
            EnginePage::Snapshot {
                through,
                items,
                more,
                at,
                ..
            } => {
                if through < 0 {
                    self.resubscribe_engine(id);
                    return;
                }
                let reset = !engine.snapshot;
                engine.snapshot = more;
                engine.through = if more { None } else { Some(through) };
                engine.ack = if more { None } else { Some(through) };
                EnginePage::Snapshot {
                    through,
                    items,
                    more,
                    at,
                    reset,
                }
            }
            EnginePage::Events {
                after,
                through,
                events,
                more,
                at,
            } => {
                if engine.snapshot
                    || engine.through != Some(after)
                    || through < after
                    || events.iter().any(|event| {
                        event.machine != machine || event.seq <= after || event.seq > through
                    })
                    || events.windows(2).any(|pair| pair[0].seq >= pair[1].seq)
                {
                    self.resubscribe_engine(id);
                    return;
                }
                engine.through = Some(through);
                EnginePage::Events {
                    after,
                    through,
                    events,
                    more,
                    at,
                }
            }
            EnginePage::Unavailable => EnginePage::Unavailable,
        };
        let _ = engine.updates.unbounded_send(EngineUpdate {
            generation: id,
            page,
        });
    }
}

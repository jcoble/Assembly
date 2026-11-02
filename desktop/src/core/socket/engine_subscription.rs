//! One owned Engine projection on the existing main socket. Applying a page and acknowledging it
//! are separate: queued replies from a replaced wire subscription can never acknowledge its successor.
use super::event::Event;
use futures_channel::mpsc::UnboundedReceiver;
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::sync::mpsc::Sender;

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct CursorAt {
    pub kind: String,
    pub entity_id: String,
    pub version: i64,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct EngineEvent {
    pub machine: String,
    pub seq: i64,
    pub kind: String,
    pub entity_kind: String,
    pub entity_id: String,
    pub version: i64,
    pub at_ms: i64,
    pub late: bool,
    pub data: Value,
}

#[derive(Debug)]
pub enum EnginePage {
    Snapshot {
        through: i64,
        items: Vec<Value>,
        more: bool,
        at: Option<CursorAt>,
        reset: bool,
    },
    Events {
        after: i64,
        through: i64,
        events: Vec<EngineEvent>,
        more: bool,
        at: Option<CursorAt>,
    },
    Unavailable,
}

#[derive(Debug)]
pub struct EngineUpdate {
    pub generation: u64,
    pub page: EnginePage,
}

pub struct EngineSubscription {
    id: u64,
    events: Sender<Event>,
    pub updates: UnboundedReceiver<EngineUpdate>,
}
impl EngineSubscription {
    pub(super) fn new(
        id: u64,
        events: Sender<Event>,
        updates: UnboundedReceiver<EngineUpdate>,
    ) -> Self {
        Self {
            id,
            events,
            updates,
        }
    }
    pub fn acknowledge(&self, update: &EngineUpdate) {
        let through = match &update.page {
            EnginePage::Snapshot {
                through,
                more: false,
                ..
            }
            | EnginePage::Events { through, .. } => *through,
            _ => return,
        };
        let _ = self.events.send(Event::AcknowledgeEngine {
            id: self.id,
            wire: update.generation,
            through,
        });
    }
    pub fn resnapshot_request(&self) -> Box<dyn Fn()> {
        let (id, events) = (self.id, self.events.clone());
        Box::new(move || {
            let _ = events.send(Event::ResnapshotEngine { id });
        })
    }
    pub fn resnapshot(&self) {
        let _ = self.events.send(Event::ResnapshotEngine { id: self.id });
    }
}
impl Drop for EngineSubscription {
    fn drop(&mut self) {
        let _ = self.events.send(Event::Unsubscribe { id: self.id });
    }
}

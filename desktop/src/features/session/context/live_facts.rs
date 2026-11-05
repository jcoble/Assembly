//! `LiveFacts` is the selected session's live facts, held in one app-wide value: its context report, its newest turn
//! figures, its agent settings and its draft images. The session pane showing the selected session publishes it on a
//! real change and clears it only while the value is still the one it published; the Context tab observes it, so the tab follows live with
//! no second subscription and no polling.

use std::sync::Arc;

use gpui_kit::*;

use crate::core::engine::SessionKey;
use crate::features::session::model::session_view::ContextReport;

#[derive(Clone, Debug, PartialEq)]
pub struct LiveFacts {
    /// The session pane that published it, which alone clears it.
    pub publisher: EntityId,
    pub session: String,
    pub key: Option<SessionKey>,
    /// The agent's name, for "not reported by NAME".
    pub provider: Option<SharedString>,
    pub context: Option<ContextReport>,
    /// From the newest `stats` row that has values; a cancelled turn's zeros don't count.
    pub input_tokens: Option<i64>,
    pub output_tokens: Option<i64>,
    pub model: Option<String>,
    pub effort: Option<String>,
    pub access: Option<String>,
    pub newest_prompt_seq: Option<i64>,
    /// How many turns have ended, so a change re-reads the session's total.
    pub turns_ended: usize,
    pub drafts: Vec<DraftImage>,
}

/// An image in the unsent draft, as the Context tab lists it.
#[derive(Clone, Debug)]
pub struct DraftImage {
    pub id: u64,
    pub name: SharedString,
    pub mime: Option<&'static str>,
    /// Where it was saved, once its upload finished.
    pub saved_path: Option<String>,
    /// The composer's own picture, so the tab reads nothing to show it.
    pub preview: Option<Arc<Image>>,
}

impl PartialEq for DraftImage {
    /// The same picture by identity, since comparing its bytes on every change would cost more than it saves.
    fn eq(&self, other: &Self) -> bool {
        self.id == other.id
            && self.name == other.name
            && self.mime == other.mime
            && self.saved_path == other.saved_path
            && match (&self.preview, &other.preview) {
                (Some(a), Some(b)) => Arc::ptr_eq(a, b),
                (None, None) => true,
                _ => false,
            }
    }
}

pub struct GlobalLiveFacts(Entity<Option<LiveFacts>>);

impl Global for GlobalLiveFacts {}

impl LiveFacts {
    /// Makes the one value, empty at first. It runs once at app start.
    pub fn init(cx: &mut App) {
        let facts = cx.new(|_| None);
        cx.set_global(GlobalLiveFacts(facts));
    }

    /// `None` where it hasn't been set up, as in test setups.
    pub fn try_global(cx: &App) -> Option<Entity<Option<LiveFacts>>> {
        cx.try_global::<GlobalLiveFacts>()
            .map(|global| global.0.clone())
    }

    /// Sets it, notifying only on a real change.
    pub fn publish(facts: LiveFacts, cx: &mut App) {
        let Some(global) = Self::try_global(cx) else {
            return;
        };
        global.update(cx, |value, cx| {
            if value.as_ref() != Some(&facts) {
                *value = Some(facts);
                cx.notify();
            }
        });
    }

    /// Clears it only if this publisher's value is still the one held.
    pub fn clear(publisher: EntityId, cx: &mut App) {
        let Some(global) = Self::try_global(cx) else {
            return;
        };
        global.update(cx, |value, cx| {
            if value
                .as_ref()
                .is_some_and(|facts| facts.publisher == publisher)
            {
                *value = None;
                cx.notify();
            }
        });
    }
}

//! The session pane's reveal of a streaming reply: where it starts, one step a tick, and what a reply draws while
//! it's revealing.

use std::time::{Duration, Instant};

use gpui_kit::*;

use super::SessionPane;
use crate::core::ui::markdown::payload::Block;
use crate::features::session::model::change::Change;
use crate::features::session::model::item::ItemKind;
use crate::features::session::model::reveal::Reveal;

/// How often the reveal shows more. Each step draws the whole window, so a reply streaming on a display's own frames
/// holds up a core on the main thread on a 120 Hz Mac. Twenty a second still reads as typing.
pub(super) const REVEAL_TICK: Duration = Duration::from_millis(50);

impl SessionPane {
    /// Starts the reveal on a reply that's streaming, or whose final row brought more text, from what it showed
    /// before (`before`), unless it's already revealing.
    pub(super) fn reveal_from(&mut self, change: Change, before: Option<usize>) {
        let at = match change {
            Change::Updated(at) => at,
            _ => self.view.items.len() - 1,
        };
        let item = &self.view.items[at];
        let ItemKind::Message { blocks, done, .. } = &item.kind else {
            return;
        };
        let grew = before.is_some_and(|shown| shown < Reveal::chars(blocks));
        if (*done && !grew)
            || self
                .reveal
                .as_ref()
                .is_some_and(|reveal| reveal.key == item.key)
        {
            return;
        }
        let shown = before.unwrap_or(0);
        self.reveal = Some(Reveal {
            key: item.key.clone(),
            shown,
        });
        self.revealed_at = Instant::now();
    }

    /// Asks for the reveal's next step, a tick from now. The task is held here, so a closed pane stops it, and it ends
    /// with its one step: once the reveal has caught up nothing asks again.
    pub(super) fn ask_reveal_tick(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.frame_asked = true;
        self._reveal_tick = Some(cx.spawn_in(window, async move |pane, cx| {
            cx.background_executor().timer(REVEAL_TICK).await;
            _ = pane.update_in(cx, |pane, window, cx| pane.reveal_frame(window, cx));
        }));
    }

    /// One step of the reveal: steps it by the time since the last, and rests once it has caught up.
    pub(super) fn reveal_frame(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.frame_asked = false;
        self._reveal_tick = None;
        let Some(reveal) = &mut self.reveal else {
            return;
        };
        let found = self
            .view
            .items
            .iter()
            .position(|item| item.key == reveal.key);
        let target = found.and_then(|at| match &self.view.items[at].kind {
            ItemKind::Message { blocks, .. } => Some(Reveal::chars(blocks)),
            _ => None,
        });
        let (Some(at), Some(target)) = (found, target) else {
            self.reveal = None;
            return;
        };
        let now = Instant::now();
        let dt = now.duration_since(self.revealed_at).as_secs_f64() * 1000.0;
        self.revealed_at = now;
        reveal.shown = Reveal::step(reveal.shown, target, dt);
        if reveal.shown >= target {
            self.reveal = None;
        } else {
            self.ask_reveal_tick(window, cx);
        }
        // Only the streaming row draws again; the timeline's entries stay as they are.
        self.redraw(at, cx);
    }

    /// The blocks a reply draws: cut to what the reveal shows while it's revealing.
    pub(super) fn shown_blocks(&self, key: &str, blocks: &[Block]) -> Option<Vec<Block>> {
        self.reveal
            .as_ref()
            .filter(|reveal| reveal.key == key)
            .map(|reveal| Reveal::clip(blocks, reveal.shown))
    }
}

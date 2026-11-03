//! The rail owns one lease per actual source/topic; all rows reuse the same held facts.
use gpui_kit::*;
use super::{RAIL_TOPIC,saved_filters,saved_view};
use crate::core::{clock::{backend_clock::BackendClock,tick::Tick},socket::client::SocketClient,state::machines::Machines};
use crate::core::socket::outcome::Outcome;
use crate::features::sessions::{rail_page::RailPage,view_options::View};
use crate::core::engine::{EngineFeed,EngineScope,SessionKey};
use crate::features::sessions::{rail_page::RailEntry,sessions_pane::SessionsPane};
fn scope(machine:&str,topic:&str)->EngineScope {EngineScope{machine:machine.into(),topic:topic.into(),key:None}}
pub(crate) fn needs_you(entry:&RailEntry,cx:&mut App)->bool {
    let Some(source)=&entry.source else{return false;};
    let feed=EngineFeed::global(cx);let feed=feed.read(cx);
    feed.loaded(&scope(&source.machine,"needs_me"))&&feed.needs_for(&SessionKey{machine:source.machine.clone(),id:entry.session_id.clone()}).iter().any(|need|need.state=="open")
}
impl SessionsPane {
    pub(in crate::features::sessions) fn set_view(&mut self, view: View, cx: &mut Context<Self>) {
        self.lines.scroll_to(ListOffset::default());
        self.from_start = true;
        self.save_view(view, cx);
        let read = self.rail.set_view(view);
        self.send(Some(read), cx);
    }

    /// Takes a read's answer, and sends the read it asks for next. The selection is left alone: a session that left
    /// the current view stays open.
    pub(in crate::features::sessions) fn answer(&mut self, id: u64, outcome: Outcome<RailPage>, cx: &mut Context<Self>) {
        let next = self.rail.finish(id, outcome);
        self.follow_engine_rows(cx);
        self.show_providers(cx);
        self.send(next, cx);
        cx.notify();
    }

    pub(super) fn follow_engine_rows(&mut self,cx:&mut Context<Self>) {
        self.follow_sources(cx);
    }
}

#[cfg(test)]
#[path="rail_source_tests.rs"]
mod tests;

impl SessionsPane {
    /// Once the backend's first ready line has started the socket: subscribes to `sessionRail`, whose state (now and
    /// after each reconnect) and every change read the rail again, holds the backend clock for the ages, and redraws
    /// as the connection and the machines change.
    pub(super) fn follow_backend(&mut self, cx: &mut Context<Self>) {
        let Some(socket) = SocketClient::try_global(cx) else {
            return cx.notify();
        };
        if self.feed.is_none() {
            let mut changes = socket.read(cx).subscribe(RAIL_TOPIC, None);
            let feed = cx.spawn(async move |pane, cx| {
                while changes.updates.recv().await.is_ok() {
                    let heard = pane.update(cx, |pane, cx| {
                        let read = pane.rail.changed();
                        pane.send(read, cx);
                    });
                    if heard.is_err() {
                        return;
                    }
                }
            });
            let clock = cx.new(|cx| {
                let mut clock = BackendClock::new();
                clock.hold(cx);
                clock
            });
            let ticks = cx.subscribe(&clock, |pane, _, tick: &Tick, cx| pane.tick(tick.now, cx));
            self.feed = Some((feed, clock, ticks));
            let mut preferences = socket.read(cx).subscribe("preferences", None);
            self.restoring = Some(cx.spawn(async move |pane, cx| {
                let first = preferences.updates.recv().await.ok();
                if let Some(first) = first.as_ref() {
                    let (providers, locations, statuses) = saved_filters(first).unwrap_or_default();
                    let view = saved_view(first);
                    pane.update(cx, |pane, cx| {
                        pane.restore(providers, locations, statuses, view, cx)
                    })
                    .ok();
                }
            }));
        }
        let mut backend = vec![cx.observe(&socket, |pane, _, cx| {pane.follow_sources(cx);cx.notify();})];
        backend.extend(
            Machines::try_global(cx).map(|machines| cx.observe(&machines, |pane, _, cx| {pane.source_cut=None;pane.follow_sources(cx);cx.notify();})),
        );
        self._backend = backend;
        self.follow_sources(cx);
        cx.notify();
    }

}

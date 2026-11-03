//! The rail owns one lease per actual source/topic; all rows reuse the same held facts.
use gpui_kit::{App,Context,ListOffset};
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
        let scopes=self.rail.sessions.iter().filter(|entry|entry.kind=="session").filter_map(|entry|entry.source.as_ref()).flat_map(|source|[scope(&source.machine,"needs_me"),scope(&source.machine,"sessions")]).collect::<std::collections::HashSet<_>>();
        self.engine_watches.retain(|scope,_|scopes.contains(scope));
        let feed=EngineFeed::global(cx);
        for scope in scopes {
            if !self.engine_watches.contains_key(&scope) {
                match feed.update(cx,|feed,cx|feed.watch(scope.clone(),cx)) {
                    Ok(watch)=>{self.engine_watches.insert(scope,watch);},
                    Err(_)=>{}, // The row keeps its recorded display; no operational readiness is inferred.
                }
            }
        }
    }
}

#[cfg(test)]
#[path="rail_source_tests.rs"]
mod tests;

//! One owned current-route discovery pass feeds the existing shared Engine scopes for all online sources.
use crate::core::{
    engine::{EngineFeed, EngineScope},
    socket::{client::SocketClient, outcome::Outcome, row_source::ExpectedRowSource},
    state::{machine::Machine, machines::Machines},
};
use crate::features::{session::client::source_resolution, sessions::sessions_pane::SessionsPane};
use gpui_kit::*;
pub(super) fn routes<'a>(machines: impl IntoIterator<Item = &'a Machine>) -> Vec<Option<String>> {
    let mut routes = machines
        .into_iter()
        .filter(|machine| machine.reachable)
        .filter_map(|machine| machine.name.clone().filter(|name| !name.trim().is_empty()))
        .map(Some)
        .collect::<Vec<_>>();
    routes.sort();
    routes.dedup();
    routes.insert(0, None);
    routes
}
impl SessionsPane {
    pub(in crate::features::sessions) fn follow_sources(&mut self, cx: &mut Context<Self>) {
        let Some(socket) = SocketClient::try_global(cx) else {
            return;
        };
        let Some(generation) = socket.read(cx).connection_generation() else {
            self.source_pass = None;
            self.source_cut = None;
            self.engine_watches.clear();
            return;
        };
        let routes = Machines::try_global(cx)
            .map(|machines| routes(machines.read(cx).machines()))
            .unwrap_or_else(|| vec![None]);
        let cut = (socket.clone(), generation, routes.clone());
        if self.source_cut.as_ref() == Some(&cut) {
            return;
        }
        self.source_pass = None;
        self.engine_watches.clear();
        let Some(serial) = self.source_serial.checked_add(1) else {
            self.source_cut = None;
            return;
        };
        self.source_serial = serial;
        self.source_cut = Some(cut.clone());
        let local = socket.read(cx).engine_machine();
        self.source_pass = Some(cx.spawn(async move |pane, cx| {
            for route in routes {
                let args = match source_resolution::build_args(route.as_deref()) {
                    Ok(args) => args,
                    Err(_) => continue,
                };
                let call = pane
                    .update(cx, |pane, cx| {
                        (pane.source_serial == serial
                            && pane.source_cut.as_ref() == Some(&cut)
                            && socket.read(cx).connection_generation() == Some(generation))
                        .then(|| {
                            socket.read(cx).call::<serde_json::Value>(
                                "sessions",
                                "resolve_source",
                                args,
                                None,
                            )
                        })
                    })
                    .ok()
                    .flatten();
                let Some(call) = call else {
                    return;
                };
                let Outcome::Ok(value) = call.await else {
                    continue;
                };
                let Ok(source) =
                    source_resolution::parse_reply(value, route.as_deref(), local.as_deref())
                else {
                    continue;
                };
                let keep = pane
                    .update(cx, |pane, cx| {
                        if pane.source_serial != serial
                            || pane.source_cut.as_ref() != Some(&cut)
                            || socket.read(cx).connection_generation() != Some(generation)
                        {
                            return false;
                        }
                        pane.hold_source(source, cx);
                        true
                    })
                    .unwrap_or(false);
                if !keep {
                    return;
                }
            }
        }));
    }
    fn hold_source(&mut self, source: ExpectedRowSource, cx: &mut Context<Self>) {
        let feed = EngineFeed::global(cx);
        feed.update(cx, |feed, _| {
            feed.name_machine(source.machine.clone(), source.route_machine.clone())
        });
        for topic in ["sessions", "needs_me"] {
            let scope = EngineScope {
                machine: source.machine.clone(),
                topic: topic.into(),
                key: None,
            };
            if self.engine_watches.contains_key(&scope) {
                continue;
            }
            if let Ok(watch) = feed.update(cx, |feed, cx| feed.watch(scope.clone(), cx)) {
                self.engine_watches.insert(scope, watch);
            }
        }
    }
}

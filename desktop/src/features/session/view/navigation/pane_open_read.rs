//! The target read and first-page row preparation of a session open, run after the open request is installed.
use super::SessionPane;
use super::opening::{call_refusal, online_historical, refused};
use crate::core::{
    engine::{EngineFeed, EngineKey, EngineRefusal},
    intents::session_open::SessionOpenTarget,
};
use crate::features::session::{
    client::{
        open_target,
        session_client::{CallError, RowUpdate},
    },
    model::open_stage::OpenStage,
    rows::row_page::RowPage,
};
use gpui_kit::*;

impl SessionPane {
    pub(super) fn start_target_read(&mut self, serial: u64, cx: &mut Context<Self>) {
        if !self.preparation_current(serial, cx) {
            self.refuse_replaced_preparation(serial, cx);
            return;
        }
        let request = self
            .pending_open
            .as_ref()
            .and_then(|pending| pending.request.clone())
            .expect("resolved request");
        let ensure = matches!(request.target, SessionOpenTarget::Engine).then(|| {
            let feed = EngineFeed::global(cx);
            feed.update(cx, |feed, cx| {
                feed.ensure_item(
                    EngineKey {
                        machine: request.key.machine.clone(),
                        kind: "session".into(),
                        id: request.key.id.clone(),
                    },
                    cx,
                )
            })
        });
        let task = cx.spawn(async move |pane, cx| {
            if let Some(ensure) = ensure {
                let result = ensure.await;
                let proceed = pane
                    .update(cx, |pane, cx| {
                        if !pane.preparation_current(serial, cx) {
                            pane.refuse_replaced_preparation(serial, cx);
                            return false;
                        }
                        match result {
                            Ok(Some(item)) => {
                                let Some(display) =
                                    crate::core::engine::engine_items::SessionItem::of(&item)
                                else {
                                    pane.cancel_preparation(
                                        refused(
                                            "invalid_engine_session",
                                            "The Engine session display is incomplete.",
                                        ),
                                        cx,
                                    );
                                    return false;
                                };
                                let pending = pane.pending_open.as_mut().expect("same preparation");
                                if pending.classify_online && online_historical(&display.state) {
                                    pending.request.as_mut().expect("resolved request").target =
                                        SessionOpenTarget::Historical;
                                }
                                pending.checked_engine = Some(item);
                                true
                            }
                            Ok(None) => {
                                pane.cancel_preparation(
                                    refused(
                                        "unknown_engine_session",
                                        "The Engine session is unavailable.",
                                    ),
                                    cx,
                                );
                                false
                            }
                            Err(error) => {
                                pane.cancel_preparation(error, cx);
                                false
                            }
                        }
                    })
                    .unwrap_or(false);
                if !proceed {
                    return;
                }
            }
            let call = pane
                .update(cx, |pane, cx| {
                    if !pane.preparation_current(serial, cx) {
                        return None;
                    }
                    let request = pane.pending_open.as_ref()?.request.as_ref()?;
                    match open_target::build_args(request, serial) {
                        Ok(args) => {
                            Some(pane.client.call("sessions", "open_target", args, None, cx))
                        }
                        Err(error) => {
                            pane.cancel_preparation(error, cx);
                            None
                        }
                    }
                })
                .ok()
                .flatten();
            let Some(call) = call else {
                return;
            };
            let answer = call.await;
            let _ = pane.update(cx, |pane, cx| pane.target_read(serial, answer, cx));
        });
        self.pending_open
            .as_mut()
            .expect("just installed request")
            .read = Some(task);
    }
    pub(super) fn preparation_current(&self, serial: u64, cx: &App) -> bool {
        self.pending_open
            .as_ref()
            .is_some_and(|pending| pending.serial == serial && pending.cut.current(cx))
    }
    fn target_read(
        &mut self,
        serial: u64,
        answer: Result<serde_json::Value, CallError>,
        cx: &mut Context<Self>,
    ) {
        if !self.preparation_current(serial, cx) {
            self.refuse_replaced_preparation(serial, cx);
            return;
        }
        let pending = self.pending_open.as_ref().expect("current request");
        let target = answer.map_err(call_refusal).and_then(|value| {
            open_target::parse_response(
                value,
                pending.request.as_ref().expect("resolved request"),
                serial,
            )
        });
        let target = match target {
            Ok(target) => target,
            Err(error) => {
                self.cancel_preparation(error, cx);
                return;
            }
        };
        if pending
            .discovered_source
            .as_ref()
            .is_some_and(|source| source != &target.source)
        {
            self.cancel_preparation(
                refused(
                    "source_changed",
                    "The discovered source changed before target validation.",
                ),
                cx,
            );
            return;
        }
        let request = pending.request.as_ref().expect("resolved request").clone();
        if self.ready_open(&request, Some(&target.source), cx)
            && target.key == request.key
            && target.read_only == self.read_only
        {
            self.pending_open = None;
            self.open_result(request, Ok(()), cx);
            return;
        }
        let session = target.key.id.clone();
        let source = target.source.clone();
        let pending = self.pending_open.as_mut().expect("current request");
        pending.stage = Some(OpenStage::new(session.clone()));
        pending.target = Some(target);
        let pane = cx.weak_entity();
        let rows = self.client.subscribe_rows(
            &session,
            None,
            Some(&source),
            Box::new(move |event, cx| {
                let _ = pane.update(cx, |pane, cx| pane.preparing_rows(serial, event, cx));
            }),
            cx,
        );
        if let Some(pending) = self
            .pending_open
            .as_mut()
            .filter(|pending| pending.serial == serial)
        {
            pending.rows = Some(rows);
        }
    }
    fn preparing_rows(&mut self, serial: u64, event: RowUpdate, cx: &mut Context<Self>) {
        if self.active_open_serial == Some(serial) {
            self.on_rows(event, cx);
            return;
        }
        if !self.preparation_current(serial, cx) {
            self.refuse_replaced_preparation(serial, cx);
            return;
        }
        match event {
            RowUpdate::Refused { code, message } => {
                self.cancel_preparation(EngineRefusal { code, message }, cx)
            }
            RowUpdate::Row(row) => {
                let result = self
                    .pending_open
                    .as_mut()
                    .and_then(|pending| pending.stage.as_mut())
                    .expect("target installed")
                    .push(*row);
                if let Err(error) = result {
                    self.cancel_preparation(refused("source_changed", &error), cx);
                } else {
                    self.workspace_if_ready(serial, cx);
                    cx.notify();
                }
            }
            RowUpdate::Jump { seq } => {
                let pending = self.pending_open.as_mut().expect("current request");
                pending.page = None;
                let ticket = match pending.stage.as_mut().expect("target installed").jump(seq) {
                    Ok(ticket) => ticket,
                    Err(error) => {
                        self.cancel_preparation(refused("invalid_row_cut", &error), cx);
                        return;
                    }
                };
                let target = pending.target.as_ref().expect("target installed");
                let args = crate::core::socket::row_source::page_args(
                    &target.key.id,
                    ticket.before,
                    100,
                    Some(&target.source),
                );
                let key = format!("session-open:{serial}");
                let call = self.client.call("rows", "page", args, Some(&key), cx);
                let page = cx.spawn(async move |pane, cx| {
                    let answer = call.await.map_err(call_refusal).and_then(|value| {
                        serde_json::from_value::<RowPage>(value)
                            .map_err(|error| refused("invalid_row_page", &error.to_string()))
                    });
                    let _ = pane.update(cx, |pane, cx| {
                        if !pane.preparation_current(serial, cx) {
                            pane.refuse_replaced_preparation(serial, cx);
                            return;
                        }
                        let result = answer.and_then(|page| {
                            pane.pending_open
                                .as_mut()
                                .and_then(|pending| pending.stage.as_mut())
                                .expect("target installed")
                                .install(ticket, page)
                                .map_err(|error| refused("invalid_row_page", &error))
                        });
                        if let Err(error) = result {
                            pane.cancel_preparation(error, cx);
                        } else {
                            pane.workspace_if_ready(serial, cx);
                            cx.notify();
                        }
                    });
                });
                self.pending_open.as_mut().expect("current request").page = Some(page);
            }
        }
    }
}

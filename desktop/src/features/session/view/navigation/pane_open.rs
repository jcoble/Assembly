//! Request-owned source validation and first-page preparation. Outgoing rows stay visible until workspace commit.
use super::SessionPane;
use crate::core::{
    engine::EngineRefusal,
    intents::session_open::{SessionOpenRequest, SessionOpenResult, SessionOpenTarget},
    socket::client::SocketClient,
    state::selected_session::SelectedSession,
};
use crate::features::session::{
    client::{
        open_target::OpenTarget,
        session_client::{CallError, RowUpdate},
    },
    model::open_stage::OpenStage,
};
use gpui_kit::*;

pub(super) struct ConnectionCut {
    main: Entity<SocketClient>,
    bulk: Entity<SocketClient>,
    main_generation: u64,
    bulk_generation: u64,
    local_owner: String,
}
impl ConnectionCut {
    pub(super) fn capture(cx: &App) -> Result<Self, EngineRefusal> {
        let main = SocketClient::try_global(cx)
            .ok_or_else(|| refused("disconnected", "The backend is not connected."))?;
        let bulk = SocketClient::try_bulk(cx)
            .ok_or_else(|| refused("disconnected", "The bulk connection is not ready."))?;
        let main_generation = main
            .read(cx)
            .connection_generation()
            .ok_or_else(|| refused("disconnected", "The backend is not ready."))?;
        let bulk_generation = bulk
            .read(cx)
            .connection_generation()
            .ok_or_else(|| refused("disconnected", "The bulk connection is not ready."))?;
        let local_owner = main.read(cx).engine_machine().ok_or_else(|| {
            refused(
                "disconnected",
                "The backend has not named its Engine owner.",
            )
        })?;
        Ok(Self {
            main,
            bulk,
            main_generation,
            bulk_generation,
            local_owner,
        })
    }
    pub(super) fn local_engine(&self, _: &App) -> Option<String> {
        Some(self.local_owner.clone())
    }
    pub(super) fn current(&self, cx: &App) -> bool {
        SocketClient::try_global(cx).is_some_and(|socket| socket == self.main)
            && SocketClient::try_bulk(cx).is_some_and(|socket| socket == self.bulk)
            && self.main.read(cx).connection_generation() == Some(self.main_generation)
            && self.bulk.read(cx).connection_generation() == Some(self.bulk_generation)
    }
}
pub(super) struct PendingOpen {
    pub checked_engine: Option<crate::core::engine::EngineItem>,
    pub classify_online: bool,
    pub serial: u64,
    pub request: Option<SessionOpenRequest>,
    pub request_id: String,
    pub discovered_source: Option<crate::core::socket::row_source::ExpectedRowSource>,
    pub cut: ConnectionCut,
    pub target: Option<OpenTarget>,
    pub stage: Option<OpenStage>,
    pub rows: Option<Subscription>,
    pub read: Option<Task<()>>,
    pub page: Option<Task<()>>,
    pub connections: Vec<Subscription>,
    pub window: AnyWindowHandle,
    pub workspace: Option<(
        u64,
        crate::features::workspaces::workspace_coordinator::WorkspaceTarget,
    )>,
    pub workspace_queued: bool,
    pub committed: bool,
    pub workspace_events: Vec<Subscription>,
}
impl SessionPane {
    fn native_card<'a>(
        &'a self,
        key: &str,
        payload_session_id: &str,
    ) -> Result<
        (
            &'a crate::core::socket::row_source::ExpectedRowSource,
            &'a crate::features::session::model::session_view::RowReference,
            &'a crate::features::session::rows::subagent::SubagentPayload,
        ),
        EngineRefusal,
    > {
        let source = self.row_source.as_ref().ok_or_else(|| {
            refused(
                "owner_validation_required",
                "The parent session has no captured source.",
            )
        })?;
        let reference = self.view.row_reference(key).ok_or_else(|| {
            refused(
                "native_card_changed",
                "The persisted native card reference is unavailable.",
            )
        })?;
        let card = self
            .view
            .items
            .iter()
            .find(|item| item.key == key)
            .and_then(|item| match &item.kind {
                crate::features::session::model::item::ItemKind::Subagent { subagent, .. }
                    if !subagent.helper =>
                {
                    Some(subagent)
                }
                _ => None,
            })
            .ok_or_else(|| refused("native_card_changed", "The persisted native card changed."))?;
        if self.session.as_deref() != Some(reference.session_id.as_str())
            || card.session_id != payload_session_id
        {
            return Err(refused(
                "native_card_changed",
                "The persisted native parent or payload changed.",
            ));
        }
        Ok((source, reference, card))
    }
    pub(super) fn native_open_request(
        &self,
        key: &str,
        payload_session_id: &str,
    ) -> Result<SessionOpenRequest, EngineRefusal> {
        let (source, reference, _) = self.native_card(key, payload_session_id)?;
        let item_id = reference.item_id.clone().ok_or_else(|| {
            refused(
                "native_card_changed",
                "The native card has no immutable row item.",
            )
        })?;
        let parent = crate::core::engine::SessionKey {
            machine: source.machine.clone(),
            id: reference.session_id.clone(),
        };
        let request = SessionOpenRequest {
            key: parent.clone(),
            request_id: uuid::Uuid::new_v4().to_string(),
            target: SessionOpenTarget::Native {
                parent,
                item_id,
                seq: reference.seq,
                payload_session_id: payload_session_id.into(),
            },
        };
        request.validate_shape()?;
        Ok(request)
    }
    pub(super) fn native_stop_task(
        &self,
        key: &str,
        payload_session_id: &str,
    ) -> Result<String, EngineRefusal> {
        let (_, reference, card) = self.native_card(key, payload_session_id)?;
        reference
            .item_id
            .as_deref()
            .and_then(|item| card.stop_target(item, reference.seq))
            .map(str::to_owned)
            .ok_or_else(|| {
                refused(
                    "native_card_changed",
                    "The native card has no current persisted Stop target.",
                )
            })
    }
    pub fn request_open(
        &mut self,
        request: SessionOpenRequest,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.cancel_preparation(
            refused("cancelled", "The open was superseded by a newer request."),
            cx,
        );
        if let Err(error) = request.validate_shape() {
            self.open_result(request, Err(error), cx);
            return;
        }
        let cut = match ConnectionCut::capture(cx) {
            Ok(cut) => cut,
            Err(error) => {
                self.open_result(request, Err(error), cx);
                return;
            }
        };
        let Some(serial) = self.open_serial.checked_add(1) else {
            self.open_result(
                request,
                Err(refused("open_limit", "Session open generation exhausted.")),
                cx,
            );
            return;
        };
        self.open_serial = serial;
        let request_id = request.request_id.clone();
        self.install_pending(Some(request), request_id, cut, serial, window, cx);
        self.start_target_read(serial, cx);
    }
    pub(super) fn ready_open(
        &self,
        request: &SessionOpenRequest,
        source: Option<&crate::core::socket::row_source::ExpectedRowSource>,
        cx: &App,
    ) -> bool {
        let same_disposition = matches!(
            (&request.target, self.read_only),
            (SessionOpenTarget::Engine, false) | (SessionOpenTarget::Historical, true)
        );
        same_disposition
            && self._rows.is_some()
            && self.held.is_none()
            && self
                .active_source_cut
                .as_ref()
                .is_some_and(|cut| cut.current(cx))
            && SelectedSession::global(cx).read(cx).key() == Some(&request.key)
            && source.is_none_or(|source| self.row_source.as_ref() == Some(source))
    }
    pub(super) fn install_pending(
        &mut self,
        request: Option<SessionOpenRequest>,
        request_id: String,
        cut: ConnectionCut,
        serial: u64,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let sockets = [cut.main.clone(), cut.bulk.clone()];
        let connections = sockets
            .into_iter()
            .map(|socket| {
                cx.subscribe(
                    &socket,
                    move |pane,
                          _,
                          event: &crate::core::socket::connection_state::ConnectionState,
                          cx| {
                        if pane.pending_open.as_ref().is_some_and(|pending| {
                            pending.serial == serial && !pending.cut.current(cx)
                        }) {
                            pane.cancel_preparation(
                                refused(
                                    "source_changed",
                                    "The captured session connection changed.",
                                ),
                                cx,
                            );
                        }
                        if pane.active_open_serial == Some(serial)
                            && pane
                                .active_source_cut
                                .as_ref()
                                .is_some_and(|cut| !cut.current(cx))
                        {
                            pane.on_rows(
                                RowUpdate::Refused {
                                    code: "source_changed".into(),
                                    message: "The captured session connection changed.".into(),
                                },
                                cx,
                            );
                        }
                        if matches!(
                            event,
                            crate::core::socket::connection_state::ConnectionState::Connected { .. }
                        ) {
                            pane.reopen_after_connection(serial, cx);
                        }
                    },
                )
            })
            .collect();
        let classify_online = request
            .as_ref()
            .is_some_and(|request| matches!(&request.target, SessionOpenTarget::Engine));
        self.pending_open = Some(PendingOpen {
            checked_engine: None,
            classify_online,
            serial,
            request,
            request_id,
            discovered_source: None,
            cut,
            target: None,
            stage: None,
            rows: None,
            read: None,
            page: None,
            connections,
            window: window.window_handle(),
            workspace: None,
            workspace_queued: false,
            committed: false,
            workspace_events: Vec::new(),
        });
    }
    pub(super) fn refuse_replaced_preparation(&mut self, serial: u64, cx: &mut Context<Self>) {
        if self
            .pending_open
            .as_ref()
            .is_some_and(|pending| pending.serial == serial)
        {
            self.cancel_preparation(
                refused("source_changed", "The captured session connection changed."),
                cx,
            );
        }
    }
    pub(super) fn cancel_preparation(&mut self, error: EngineRefusal, cx: &mut Context<Self>) {
        let Some(mut pending) = self.pending_open.take() else {
            return;
        };
        if let Some(stage) = pending.stage.as_mut() {
            stage.invalidate();
        }
        if pending.committed {
            self.composer
                .update(cx, |composer, cx| composer.invalidate_source(cx));
            self._rows = None;
            self._page = None;
            self._loads.clear();
            self.status = error.message.clone().into();
        }
        if let Some((generation, _)) = pending.workspace.take()
            && let Some(coordinator) =
                crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator::try_global(
                    cx,
                )
        {
            coordinator.update(cx, |coordinator, cx| coordinator.cancel(generation, cx));
        }
        if let Some(request) = pending.request {
            self.open_result(request, Err(error), cx);
        } else {
            self.unknown_open_result(pending.request_id, error, cx);
        }
        cx.notify();
    }
    pub(super) fn unknown_open_result(
        &mut self,
        request_id: String,
        error: EngineRefusal,
        cx: &mut Context<Self>,
    ) {
        SelectedSession::global(cx).update(cx, |_, cx| {
            cx.emit(SessionOpenResult {
                request_id,
                key: None,
                result: Err(error),
            })
        });
    }
    pub(super) fn open_result(
        &mut self,
        request: SessionOpenRequest,
        result: Result<(), EngineRefusal>,
        cx: &mut Context<Self>,
    ) {
        SelectedSession::global(cx).update(cx, |_, cx| {
            cx.emit(SessionOpenResult {
                request_id: request.request_id,
                key: Some(request.key),
                result,
            })
        });
    }
}
pub(super) fn refused(code: &str, message: &str) -> EngineRefusal {
    EngineRefusal {
        code: code.into(),
        message: message.into(),
    }
}
pub(super) fn call_refusal(error: CallError) -> EngineRefusal {
    match error {
        CallError::Refused { code, message } => EngineRefusal { code, message },
        error => refused("session_open_failed", &error.reason()),
    }
}

/// Whether a session's row, changed since an open checked it, still opens the same way. A session's row changes all the
/// time while it runs, and comes again after a reconnect; only what the open was built on can stop it.
pub(super) fn opens_the_same(
    checked: &crate::core::engine::EngineItem,
    current: &crate::core::engine::EngineItem,
) -> bool {
    use crate::core::engine::engine_items::SessionItem;
    match (SessionItem::of(checked), SessionItem::of(current)) {
        (Some(checked), Some(current)) => {
            checked.provider == current.provider
                && checked.parent_id == current.parent_id
                && checked.parent_machine == current.parent_machine
                && online_historical(&checked.state) == online_historical(&current.state)
        }
        _ => false,
    }
}

pub(super) fn online_historical(state: &str) -> bool {
    matches!(state, "ended" | "lost" | "moved" | "handing_off")
}

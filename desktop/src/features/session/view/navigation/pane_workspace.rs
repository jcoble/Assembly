//! The sole workspace transition calls back only after its outgoing guard and incoming validation.
use super::{SessionPane, opening::refused};
use crate::core::{
    state::{selected_session::SelectedSession, workspace_panes::WorkspaceKey},
    ui::center_panes::CenterPanes,
};
use crate::features::session::model::change::Change;
use crate::features::workspaces::workspace_coordinator::{
    WorkspaceActivated, WorkspaceCoordinator, WorkspaceRefused, WorkspaceTarget,
};
use gpui_kit::*;

impl SessionPane {
    /// Returns the actual existing Chat composer only for a matching completed canonical open.
    pub fn composer_for_open(
        key: &crate::core::engine::SessionKey,
        request_id: &str,
        cx: &App,
    ) -> Result<Entity<crate::features::session::composer::composer_view::Composer>, String> {
        let pane =
            CenterPanes::find::<SessionPane>("Chat", cx).ok_or("The Chat pane is unavailable.")?;
        let pane = pane.read(cx);
        let matches = pane.active_reopen.as_ref().is_some_and(|(request, _)| {
            request.request_id == request_id
                && request.key == *key
                && matches!(
                    request.target,
                    crate::core::intents::session_open::SessionOpenTarget::Engine
                )
        });
        if !matches
            || pane.read_only
            || pane.pending_open.is_some()
            || pane.session.as_deref() != Some(key.id.as_str())
            || pane
                .row_source
                .as_ref()
                .is_none_or(|source| source.machine != key.machine)
            || !pane
                .active_source_cut
                .as_ref()
                .is_some_and(|cut| cut.current(cx))
        {
            return Err("The first prompt no longer matches the completed session open.".into());
        }
        Ok(pane.composer.clone())
    }

    pub(super) fn workspace_if_ready(&mut self, serial: u64, cx: &mut Context<Self>) {
        let Some(pending) = self
            .pending_open
            .as_mut()
            .filter(|pending| pending.serial == serial && !pending.workspace_queued)
        else {
            return;
        };
        if !pending.stage.as_ref().is_some_and(|stage| stage.ready()) {
            return;
        }
        pending.workspace_queued = true;
        let handle = pending.window;
        let pane = cx.weak_entity();
        // Release this pane's borrow before request: an already open workspace may invoke commit synchronously.
        cx.defer(move |cx| {
            let _ = handle.update(cx, |_, window, cx| {
                let Some(coordinator) = WorkspaceCoordinator::try_global(cx) else {
                    let _ = pane.update(cx, |pane, cx| {
                        pane.cancel_preparation(
                            refused(
                                "workspace_unavailable",
                                "The workspace coordinator is not ready.",
                            ),
                            cx,
                        )
                    });
                    return;
                };
                let target = pane
                    .update(cx, |pane, cx| {
                        if !pane.preparation_current(serial, cx) {
                            return None;
                        }
                        let pending = pane.pending_open.as_mut()?;
                        if !pending.stage.as_ref().is_some_and(|stage| stage.ready()) {
                            pending.workspace_queued = false;
                            return None;
                        }
                        let target = pending.target.as_ref()?;
                        let target = match target
                            .recorded_checkout_root
                            .as_ref()
                            .filter(|root| !root.trim().is_empty())
                        {
                            Some(root) => WorkspaceTarget::Checkout(WorkspaceKey {
                                machine: target.key.machine.clone(),
                                session_id: target.key.id.clone(),
                                checkout_root: root.clone(),
                            }),
                            None => WorkspaceTarget::TranscriptOnly(target.key.clone()),
                        };
                        pending.workspace_events = vec![
                            cx.subscribe(
                                &coordinator,
                                move |pane, _, event: &WorkspaceActivated, cx| {
                                    pane.workspace_activated(serial, event, cx)
                                },
                            ),
                            cx.subscribe(
                                &coordinator,
                                move |pane, _, event: &WorkspaceRefused, cx| {
                                    if pane.pending_open.as_ref().is_some_and(|pending| {
                                        pending.serial == serial
                                            && pending.workspace.as_ref()
                                                == Some(&(event.generation, event.target.clone()))
                                    }) {
                                        pane.cancel_preparation(
                                            refused("workspace_refused", &event.reason),
                                            cx,
                                        );
                                    }
                                },
                            ),
                        ];
                        Some(target)
                    })
                    .ok()
                    .flatten();
                let Some(target) = target else {
                    return;
                };
                let commit_pane = pane.clone();
                let expected = target.clone();
                let generation = coordinator.update(cx, |coordinator, cx| {
                    coordinator.request(
                        target.clone(),
                        Box::new(move |actual, window, cx| {
                            if actual != &expected {
                                return Err("The workspace open target changed.".into());
                            }
                            commit_pane
                                .update(cx, |pane, cx| pane.commit_prepared(serial, window, cx))
                                .map_err(|_| "The session pane closed.".to_string())?
                        }),
                        window,
                        cx,
                    )
                });
                let _ = pane.update(cx, |pane, _| {
                    if let Some(pending) = pane
                        .pending_open
                        .as_mut()
                        .filter(|pending| pending.serial == serial)
                    {
                        pending.workspace = Some((generation, target));
                    }
                });
            });
        });
    }
    fn commit_prepared(
        &mut self,
        serial: u64,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Result<(), String> {
        if !self.preparation_current(serial, cx) {
            return Err("The captured session connection changed.".into());
        }
        let pending = self
            .pending_open
            .as_mut()
            .ok_or("The prepared session open was cancelled.")?;
        if let Some(checked) = &mut pending.checked_engine {
            let feed = crate::core::engine::EngineFeed::global(cx);
            if let Some(current) = feed.read(cx).item(&checked.key) {
                if !super::opening::opens_the_same(checked, current) {
                    return Err(
                        "The checked Engine session changed while preparing its workspace.".into(),
                    );
                }
                *checked = current.clone();
            }
        }
        let target = pending
            .target
            .as_ref()
            .ok_or("The session target is unavailable.")?;
        let stage = pending
            .stage
            .as_mut()
            .ok_or("The session page is unavailable.")?;
        if !stage.ready() {
            return Err("The session first page is no longer coherent.".into());
        }
        let prepared = stage.take()?;
        let key = target.key.clone();
        let source = target.source.clone();
        let read_only = target.read_only;
        let parent = match pending.request.as_ref().map(|request| &request.target) {
            Some(crate::core::intents::session_open::SessionOpenTarget::Native {
                parent, ..
            }) => Some(parent.clone()),
            _ => pending
                .checked_engine
                .as_ref()
                .and_then(crate::core::engine::engine_items::SessionItem::of)
                .and_then(|item| {
                    Some(crate::core::engine::SessionKey {
                        machine: item.parent_machine?,
                        id: item.parent_id?,
                    })
                }),
        };
        let provider = pending
            .checked_engine
            .as_ref()
            .and_then(crate::core::engine::engine_items::SessionItem::of)
            .map(|item| item.provider);
        pending.committed = true;
        // Every fallible identity/page check precedes mutation. The prepared watch becomes the live watch unchanged.
        self._rows = pending.rows.take();
        self._page = None;
        self._loads.clear();
        self._source_connections.clear();
        self.active_source_cut = None;
        // Another session opening takes the pane from a draft; the same one reopening after a reconnect doesn't.
        if !self.shows(&key) {
            self.draft = None;
        }
        self.session = Some(key.id.clone());
        self.row_source = Some(source.clone());
        self.read_only = read_only;
        self.active_provider = provider;
        self.active_open_serial = Some(serial);
        self.view = prepared.view;
        self.known_run = prepared.known_run;
        self.read_to = 0;
        self.held = None;
        self.loading = false;
        self.retry_first = None;
        self.failed_before = None;
        self.status = SharedString::default();
        self.open_folds.clear();
        self.kept.clear();
        self.fresh.clear();
        self.forms.clear();
        self.reading.clear();
        self.expands.clear();
        self.open_asks.clear();
        self.growing.clear();
        self.reveal = None;
        self.plan_open = false;
        self.header.update(cx, |header, cx| {
            header.show(Some(key.id.clone()), cx);
            header.set_parent(parent, cx);
        });
        self.timeline.update(cx, |timeline, cx| {
            timeline.set_session(Some(key.id.clone()), Vec::new(), cx)
        });
        self.composer.update(cx, |composer, cx| {
            composer.set_target(
                (!read_only).then(|| key.clone()),
                (!read_only).then(|| source.clone()),
                window,
                cx,
            )
        });
        SelectedSession::global(cx).update(cx, |selected, cx| {
            selected.commit_open(key, source, read_only, cx)
        });
        self.tell_offline(cx);
        self.refresh(Change::Nothing, cx);
        Ok(())
    }
    fn workspace_activated(
        &mut self,
        serial: u64,
        event: &WorkspaceActivated,
        cx: &mut Context<Self>,
    ) {
        if !self.pending_open.as_ref().is_some_and(|pending| {
            pending.serial == serial
                && pending.committed
                && pending.workspace.as_ref() == Some(&(event.generation, event.target.clone()))
        }) {
            return;
        }
        let pending = self
            .pending_open
            .take()
            .expect("matching activated session");
        self.active_reopen = Some((
            pending
                .request
                .as_ref()
                .expect("activated canonical request")
                .clone(),
            pending.window,
        ));
        self.reopen_queued = None;
        self._source_connections = pending.connections;
        self.active_source_cut = Some(pending.cut);
        let result = if matches!(&event.target, WorkspaceTarget::Checkout(_)) && !event.restored {
            let message = WorkspaceCoordinator::try_global(cx)
                .and_then(|coordinator| coordinator.read(cx).status.clone())
                .unwrap_or_else(|| {
                    "The target transcript is visible, but its workspace panes did not restore."
                        .into()
                });
            self.status = message.clone().into();
            Err(refused("workspace_restore_failed", &message))
        } else {
            Ok(())
        };
        self.open_result(
            pending.request.expect("activated canonical request"),
            result,
            cx,
        );
        // Merely preparing a target never changes the tab. Selection is now committed and codecs have restored.
        let handle = pending.window;
        cx.defer(move |cx| {
            let _ = handle.update(cx, |_, window, cx| {
                if let Some(chat) = CenterPanes::find::<SessionPane>("Chat", cx) {
                    CenterPanes::open("Chat", window, cx, |_, _| chat);
                }
            });
        });
    }
}

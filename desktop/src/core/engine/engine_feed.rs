//! The connection's shared display cache. Consumers own leases; the last lease
//! drops the one scope task and its existing socket subscription.
use super::{
    EngineCursor, EngineItem, EngineKey, EngineRefusal, EngineScope, EngineStore, SessionKey,
};
use crate::core::socket::client::SocketClient;
use crate::core::socket::engine_update::EngineUpdate;
use futures_channel::mpsc;
use gpui_kit::*;
use std::cell::Cell;
use std::collections::HashMap;
use std::rc::{Rc, Weak};

#[derive(Clone, Debug)]
pub struct EngineChanged {
    pub scopes: Vec<EngineScope>,
    pub keys: Vec<EngineKey>,
    pub sessions: Vec<SessionKey>,
}
struct ScopeWatch {
    _task: Task<()>,
    active: Rc<Cell<bool>>,
    coherent: Rc<Cell<bool>>,
    resnapshot: Box<dyn Fn()>,
}
struct Running(Rc<Cell<bool>>);
impl Drop for Running {
    fn drop(&mut self) {
        self.0.set(false);
    }
}
#[derive(Clone)]
pub struct EngineWatch {
    _watch: Rc<ScopeWatch>,
}

impl EngineWatch {
    pub fn request_snapshot(&self) {
        if self._watch.active.get() {
            (self._watch.resnapshot)();
        }
    }
}

#[derive(Default)]
pub struct EngineFeed {
    pub(super) store: EngineStore,
    watches: HashMap<EngineScope, Weak<ScopeWatch>>,
    pub(super) native_watches: HashMap<SessionKey, Weak<super::native_activity::NativeScope>>,
    error: Option<EngineRefusal>,
}
struct GlobalEngine(Entity<EngineFeed>);
impl Global for GlobalEngine {}
impl EventEmitter<EngineChanged> for EngineFeed {}

impl EngineFeed {
    pub fn global(cx: &mut App) -> Entity<Self> {
        if let Some(global) = cx.try_global::<GlobalEngine>() {
            return global.0.clone();
        }
        let feed = cx.new(|_| Self::default());
        cx.set_global(GlobalEngine(feed.clone()));
        feed
    }
    pub fn item(&self, key: &EngineKey) -> Option<&EngineItem> {
        self.store.item(key)
    }
    pub fn items(&self, scope: &EngineScope) -> Vec<EngineItem> {
        self.store.items(scope)
    }
    pub fn need(&self, key: &EngineKey) -> Option<super::engine_items::NeedItem> {
        self.store
            .item(key)
            .and_then(super::engine_items::NeedItem::of)
    }
    pub fn needs_for(&self, session: &SessionKey) -> Vec<super::engine_items::NeedItem> {
        self.store
            .needs_for(session)
            .into_iter()
            .filter_map(super::engine_items::NeedItem::of)
            .collect()
    }
    pub fn session(&self, key: &SessionKey) -> Option<super::engine_items::SessionItem> {
        self.store
            .item(&EngineKey {
                machine: key.machine.clone(),
                kind: "session".into(),
                id: key.id.clone(),
            })
            .and_then(super::engine_items::SessionItem::of)
    }
    pub fn children_of(&self, parent: &SessionKey) -> Vec<super::engine_items::SessionItem> {
        self.store
            .children_of(parent)
            .into_iter()
            .filter_map(super::engine_items::SessionItem::of)
            .collect()
    }
    pub fn parent_of(&self, child: &SessionKey) -> Option<SessionKey> {
        self.store.parent_of(child)
    }
    pub fn loaded(&self, scope: &EngineScope) -> bool {
        self.store.cursor(scope).is_some()
            && self
                .watches
                .get(scope)
                .and_then(Weak::upgrade)
                .is_some_and(|watch| watch.active.get() && watch.coherent.get())
    }
    pub fn error(&self) -> Option<&EngineRefusal> {
        self.error.as_ref()
    }

    /// Read the exact source/key through the same bounded bulk connection. Unheld rows
    /// are returned to the caller without inventing a stream cursor or cache lease.
    pub fn ensure_item(
        &mut self,
        key: EngineKey,
        cx: &mut Context<Self>,
    ) -> Task<Result<Option<EngineItem>, EngineRefusal>> {
        cx.spawn(async move |feed, cx| {
            let rows = super::entity_batch::read(std::slice::from_ref(&key), cx).await?;
            match feed.update(cx, |feed, cx| {
                let old_session = feed
                    .store
                    .item(&key)
                    .and_then(|item| item.value["sessionId"].as_str())
                    .map(str::to_owned);
                let result = feed.store.apply_entity(&key, rows)?;
                let session = if key.kind == "session" {
                    Some(key.id.clone())
                } else {
                    result
                        .as_ref()
                        .and_then(|item| item.value["sessionId"].as_str())
                        .map(str::to_owned)
                        .or(old_session)
                };
                let sessions = session
                    .into_iter()
                    .map(|id| SessionKey {
                        machine: key.machine.clone(),
                        id,
                    })
                    .collect();
                cx.emit(EngineChanged {
                    scopes: Vec::new(),
                    keys: vec![key],
                    sessions,
                });
                cx.notify();
                Ok(result)
            }) {
                Ok(result) => result,
                Err(_) => Err(EngineRefusal {
                    code: "cancelled".into(),
                    message: "The view was closed.".into(),
                }),
            }
        })
    }

    /// Machine must be an actual Engine ID from the owning connection/mirror.
    /// A missing identity cannot silently fall back to a local or saved name.
    pub fn watch(
        &mut self,
        scope: EngineScope,
        cx: &mut Context<Self>,
    ) -> Result<EngineWatch, EngineRefusal> {
        if scope.machine.is_empty() {
            return Err(EngineRefusal {
                code: "missing_engine_identity".into(),
                message: "The owning Engine identity is not available.".into(),
            });
        }
        if let Some(watch) = self
            .watches
            .get(&scope)
            .and_then(Weak::upgrade)
            .filter(|watch| watch.active.get())
        {
            return Ok(EngineWatch { _watch: watch });
        }
        let socket = SocketClient::try_global(cx).ok_or_else(|| EngineRefusal {
            code: "disconnected".into(),
            message: "The backend is not connected.".into(),
        })?;
        let mut subscription = socket.read(cx).subscribe_engine(
            &scope.topic,
            scope.key.as_deref(),
            Some(&scope.machine),
        );
        let coherent = Rc::new(Cell::new(false));
        let current_wire = Rc::new(Cell::new(0_u64));
        let retired_wire = Rc::new(Cell::new(0_u64));
        let snapshot_pending = Rc::new(Cell::new(false));
        let refresh_revision = Rc::new(Cell::new(0_u64));
        let reader_wake = Rc::new(Cell::new(None::<std::task::Waker>));
        let reset = subscription.resnapshot_request();
        let resnapshot: Rc<dyn Fn()> = {
            let (coherent, current_wire, retired_wire) =
                (coherent.clone(), current_wire.clone(), retired_wire.clone());
            let pending = snapshot_pending.clone();
            let (revision, reader_wake) = (refresh_revision.clone(), reader_wake.clone());
            Rc::new(move || {
                if pending.replace(true) {
                    return;
                }
                coherent.set(false);
                retired_wire.set(retired_wire.get().max(current_wire.get()));
                revision.set(revision.get().wrapping_add(1));
                if let Some(wake) = reader_wake.take() {
                    wake.wake();
                }
                reset();
            })
        };
        let reset_on_connect = resnapshot.clone();
        let disconnected = coherent.clone();
        let pending_connection = snapshot_pending.clone();
        let invalidated_scope = scope.clone();
        let main_connection = cx.subscribe(&socket, move |_, _, state, cx| {
            if matches!(
                state,
                crate::core::socket::connection_state::ConnectionState::Connected { .. }
            ) {
                reset_on_connect();
            } else {
                disconnected.set(false);
                pending_connection.set(false);
                reset_on_connect();
            }
            cx.emit(EngineChanged {
                scopes: vec![invalidated_scope.clone()],
                keys: Vec::new(),
                sessions: Vec::new(),
            });
            cx.notify();
        });
        let applied_snapshot = coherent.clone();
        let watched = scope.clone();
        let bulk = SocketClient::try_bulk(cx);
        // Coalesce readiness notifications. Reads inspect the actual current generation;
        // a queued older signal cannot hide a newer connection or trigger polling.
        let (mut ready, mut readiness) = mpsc::channel(1);
        let connection = bulk.as_ref().map(|bulk| {
            cx.subscribe(bulk, move |_, _, state, _| {
                if matches!(
                    state,
                    crate::core::socket::connection_state::ConnectionState::Connected { .. }
                ) {
                    let _ = ready.try_send(());
                }
            })
        });
        let active = Rc::new(Cell::new(true));
        let running = Running(active.clone());
        let task = cx.spawn(async move |feed, cx| {
            let _running = running;
            let _connection = connection;
            let _main_connection = main_connection;
            'updates: while let Ok(update) = subscription.updates.recv().await {
                if let EngineUpdate::Refused { code, message } = update {
                    applied_snapshot.set(false);
                    let _ = feed.update(cx, |feed, cx| {
                        feed.error = Some(EngineRefusal { code, message });
                        cx.emit(EngineChanged {
                            scopes: vec![watched.clone()],
                            keys: Vec::new(),
                            sessions: Vec::new(),
                        });
                        cx.notify();
                    });
                    return;
                }
                let EngineUpdate::Scoped(page) = update else {
                    continue;
                };
                if page.machine != watched.machine
                    || page.wire <= retired_wire.get()
                    || (!page.snapshot && !applied_snapshot.get())
                {
                    continue;
                }
                current_wire.set(page.wire);
                let snapshot = page.snapshot;
                let wire = page.wire;
                let through = page.through;
                let at = page.at.clone();
                let refs = if page.snapshot {
                    Vec::new()
                } else {
                    match feed.update(cx, |feed, _| {
                        feed.store.event_refs(
                            &watched,
                            &EngineCursor {
                                through: page.through,
                                at: page.at.clone(),
                            },
                            &page.events,
                        )
                    }) {
                        Ok(Ok(refs)) => refs,
                        Ok(Err(error)) => {
                            let _ = feed.update(cx, |feed, cx| {
                                feed.error = Some(error);
                                cx.notify();
                            });
                            return;
                        }
                        Err(_) => return,
                    }
                };
                if wire <= retired_wire.get() {
                    continue;
                }
                let revision = refresh_revision.get();
                let display = {
                    let mut pending = std::pin::pin!(async {
                        Some(loop {
                            let attempted = cx.update(|cx| {
                                bulk.as_ref()
                                    .and_then(|bulk| bulk.read(cx).connection_generation())
                            });
                            match super::entity_batch::read(&refs, cx).await {
                                Ok(rows) => break rows,
                                Err(error) => {
                                    let reconnect = error.code == "entity_connection_changed";
                                    let _ = feed.update(cx, |feed, cx| {
                                        feed.error = Some(error);
                                        cx.notify();
                                    });
                                    if !reconnect {
                                        return None;
                                    }
                                    // This harmless read resumes only after a pushed, different
                                    // bulk generation. The original stream cut remains unapplied.
                                    loop {
                                        let current = cx.update(|cx| {
                                            bulk.as_ref().and_then(|bulk| {
                                                bulk.read(cx).connection_generation()
                                            })
                                        });
                                        if current.is_some() && current != attempted {
                                            break;
                                        }
                                        if readiness.recv().await.is_err() {
                                            return None;
                                        }
                                    }
                                }
                            }
                        })
                    });
                    std::future::poll_fn(|wake| {
                        if refresh_revision.get() != revision {
                            return std::task::Poll::Ready(None);
                        }
                        match std::future::Future::poll(pending.as_mut(), wake) {
                            std::task::Poll::Ready(rows) => std::task::Poll::Ready(Some(rows)),
                            std::task::Poll::Pending => {
                                if refresh_revision.get() != revision {
                                    return std::task::Poll::Ready(None);
                                }
                                reader_wake.set(Some(wake.waker().clone()));
                                std::task::Poll::Pending
                            }
                        }
                    })
                    .await
                };
                reader_wake.take();
                let display = match display {
                    Some(Some(rows)) => rows,
                    Some(None) => return,
                    None => continue 'updates,
                };
                if wire <= retired_wire.get() {
                    continue;
                }
                let result = feed.update(cx, |feed, cx| {
                    let cursor = EngineCursor {
                        through: page.through,
                        at: page.at,
                    };
                    let changed = if page.snapshot {
                        feed.store.snapshot(watched.clone(), cursor, page.items)
                    } else {
                        feed.store
                            .apply_event_rows(&watched, cursor, &page.events, display)
                    };
                    let applied = match changed {
                        Ok(keys) => {
                            if snapshot {
                                applied_snapshot.set(true);
                                snapshot_pending.set(false);
                            }
                            let mut sessions = Vec::new();
                            for key in &keys {
                                let id = if key.kind == "session" {
                                    Some(key.id.as_str())
                                } else {
                                    feed.store
                                        .item(key)
                                        .and_then(|item| item.value["sessionId"].as_str())
                                };
                                if let Some(id) = id {
                                    let session = SessionKey {
                                        machine: key.machine.clone(),
                                        id: id.into(),
                                    };
                                    if !sessions.contains(&session) {
                                        sessions.push(session);
                                    }
                                }
                            }
                            feed.error = None;
                            if page.snapshot || !keys.is_empty() {
                                cx.emit(EngineChanged {
                                    scopes: vec![watched.clone()],
                                    keys,
                                    sessions,
                                });
                            }
                            true
                        }
                        Err(error) => {
                            feed.error = Some(error);
                            false
                        }
                    };
                    cx.notify();
                    applied
                });
                match result {
                    Ok(true) => subscription.ack_engine(wire, through, at),
                    Ok(false) | Err(_) => return,
                }
            }
        });
        let watch = Rc::new(ScopeWatch {
            _task: task,
            active,
            coherent,
            resnapshot: Box::new(move || resnapshot()),
        });
        self.watches.insert(scope, Rc::downgrade(&watch));
        Ok(EngineWatch { _watch: watch })
    }
}

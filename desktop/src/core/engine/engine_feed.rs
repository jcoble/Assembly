//! The connection's shared display cache. Consumers own leases; the last lease
//! drops the one scope task and its existing socket subscription.
use std::collections::HashMap;
use std::rc::{Rc, Weak};
use gpui_kit::*;
use super::{EngineCursor, EngineItem, EngineKey, EngineRefusal, EngineScope, EngineStore, SessionKey};
use crate::core::socket::client::SocketClient;
use crate::core::socket::engine_update::EngineUpdate;

#[derive(Clone, Debug)]
pub struct EngineChanged {
    pub scopes: Vec<EngineScope>,
    pub keys: Vec<EngineKey>,
    pub sessions: Vec<SessionKey>,
}
struct ScopeWatch { _task: Task<()> }
#[derive(Clone)]
pub struct EngineWatch { _watch: Rc<ScopeWatch> }

#[derive(Default)]
pub struct EngineFeed {
    store: EngineStore,
    watches: HashMap<EngineScope, Weak<ScopeWatch>>,
    error: Option<EngineRefusal>,
}
struct GlobalEngine(Entity<EngineFeed>);
impl Global for GlobalEngine {}
impl EventEmitter<EngineChanged> for EngineFeed {}

impl EngineFeed {
    pub fn global(cx: &mut App) -> Entity<Self> {
        if let Some(global) = cx.try_global::<GlobalEngine>() { return global.0.clone(); }
        let feed = cx.new(|_| Self::default());
        cx.set_global(GlobalEngine(feed.clone()));
        feed
    }
    pub fn item(&self, key: &EngineKey) -> Option<&EngineItem> { self.store.item(key) }
    pub fn items(&self, scope: &EngineScope) -> Vec<EngineItem> { self.store.items(scope) }
    pub fn loaded(&self, scope: &EngineScope) -> bool { self.store.cursor(scope).is_some() }
    pub fn error(&self) -> Option<&EngineRefusal> { self.error.as_ref() }

    /// Machine must be an actual Engine ID from the owning connection/mirror.
    /// A missing identity cannot silently fall back to a local or saved name.
    pub fn watch(&mut self, scope: EngineScope, cx: &mut Context<Self>) -> Result<EngineWatch, EngineRefusal> {
        if scope.machine.is_empty() {
            return Err(EngineRefusal { code:"missing_engine_identity".into(), message:"The owning Engine identity is not available.".into() });
        }
        if let Some(watch) = self.watches.get(&scope).and_then(Weak::upgrade) {
            return Ok(EngineWatch { _watch: watch });
        }
        let socket = SocketClient::try_global(cx).ok_or_else(|| EngineRefusal {
            code:"disconnected".into(), message:"The backend is not connected.".into(),
        })?;
        let mut subscription = socket.read(cx).subscribe_engine(&scope.topic, scope.key.as_deref(), Some(&scope.machine));
        let watched = scope.clone();
        let task = cx.spawn(async move |feed, cx| {
            while let Ok(update) = subscription.updates.recv().await {
                let EngineUpdate::Scoped(page) = update else { continue; };
                if page.machine != watched.machine { continue; }
                let wire = page.wire;
                let through = page.through;
                let at = page.at.clone();
                let result = feed.update(cx, |feed, cx| {
                    let cursor = EngineCursor { through:page.through, at:page.at };
                    let changed = if page.snapshot {
                        feed.store.snapshot(watched.clone(), cursor, page.items)
                    } else { feed.store.events(&watched, cursor, &page.events) };
                    let applied = match changed {
                        Ok(keys) => {
                            let mut sessions = Vec::new();
                            for key in &keys {
                                let id = if key.kind == "session" { Some(key.id.as_str()) }
                                    else { feed.store.item(key).and_then(|item| item.value["sessionId"].as_str()) };
                                if let Some(id) = id {
                                    let session = SessionKey { machine:key.machine.clone(), id:id.into() };
                                    if !sessions.contains(&session) { sessions.push(session); }
                                }
                            }
                            feed.error = None;
                            if page.snapshot || !keys.is_empty() {
                                cx.emit(EngineChanged { scopes:vec![watched.clone()], keys, sessions });
                            }
                            true
                        }
                        Err(error) => { feed.error = Some(error); false }
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
        let watch = Rc::new(ScopeWatch { _task:task });
        self.watches.insert(scope, Rc::downgrade(&watch));
        Ok(EngineWatch { _watch:watch })
    }
}

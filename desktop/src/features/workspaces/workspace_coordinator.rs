//! One revision-fenced workspace transition. The navigator supplies an already validated owning checkout key.
use crate::core::socket::{client::SocketClient, outcome::Outcome, update::Update};
pub use crate::core::state::workspace_panes::WorkspaceKey;
use crate::core::state::workspace_panes::WorkspacePanes;
use gpui_kit::*;
use serde::Deserialize;
use serde_json::{Value, json};

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum WorkspaceTarget {
    Checkout(WorkspaceKey),
    TranscriptOnly(crate::core::engine::SessionKey),
}
impl WorkspaceTarget {
    pub fn checkout(&self) -> Option<&WorkspaceKey> {
        match self {
            Self::Checkout(key) => Some(key),
            Self::TranscriptOnly(_) => None,
        }
    }
    fn valid(&self) -> bool {
        match self {
            Self::Checkout(key) => {
                !key.machine.is_empty()
                    && !key.session_id.is_empty()
                    && !key.checkout_root.is_empty()
            }
            Self::TranscriptOnly(key) => !key.machine.is_empty() && !key.id.is_empty(),
        }
    }
}
#[derive(Clone, Debug)]
pub struct WorkspaceActivated {
    pub generation: u64,
    pub target: WorkspaceTarget,
    pub restored: bool,
}
#[derive(Clone, Debug)]
pub struct WorkspaceRefused {
    pub generation: u64,
    pub target: WorkspaceTarget,
    pub reason: String,
}
struct Open {
    key: WorkspaceKey,
    revision: u64,
    writable: bool,
    safe_to_leave: Option<Value>,
}
#[derive(Deserialize)]
struct Read {
    snapshot: Option<Value>,
    revision: u64,
}
#[derive(Deserialize)]
struct Saved {
    revision: u64,
}
pub type WorkspaceCommit =
    Box<dyn FnOnce(&WorkspaceTarget, &mut Window, &mut App) -> Result<(), String>>;
struct Current(Entity<WorkspaceCoordinator>);
impl Global for Current {}
pub struct WorkspaceCoordinator {
    current: Option<Open>,
    pending: Option<WorkspaceTarget>,
    commit: Option<WorkspaceCommit>,
    generation: u64,
    active_request: bool,
    migrating: Option<Task<()>>,
    migrated: Option<migration::Completed>,
    activated_target: Option<WorkspaceTarget>,
    saving: Option<Task<()>>,
    reading: Option<Task<()>>,
    guarding: Option<Task<()>>,
    watching: Option<Task<()>>,
    pub status: Option<String>,
}
impl EventEmitter<WorkspaceActivated> for WorkspaceCoordinator {}
impl EventEmitter<WorkspaceRefused> for WorkspaceCoordinator {}
impl WorkspaceCoordinator {
    pub fn init(cx: &mut App) -> Entity<Self> {
        if let Some(current) = cx.try_global::<Current>() {
            return current.0.clone();
        }
        let view = cx.new(|_| Self {
            current: None,
            pending: None,
            commit: None,
            generation: 0,
            active_request: false,
            migrating: None,
            migrated: None,
            activated_target: None,
            saving: None,
            reading: None,
            guarding: None,
            watching: None,
            status: None,
        });
        cx.set_global(Current(view.clone()));
        view
    }
    pub fn try_global(cx: &App) -> Option<Entity<Self>> {
        cx.try_global::<Current>().map(|current| current.0.clone())
    }
    pub fn current_target(&self) -> Option<&WorkspaceTarget> {
        self.activated_target.as_ref()
    }
    pub fn current_key(&self) -> Option<&WorkspaceKey> {
        self.current.as_ref().map(|current| &current.key)
    }
    /// Key authority belongs to the source-qualified navigator. This method never derives a root from display metadata.
    pub fn request(
        &mut self,
        target: WorkspaceTarget,
        commit: WorkspaceCommit,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> u64 {
        self.refuse(
            "This workspace open was superseded by a newer request.".into(),
            cx,
        );
        self.active_request = true;
        self.generation = self.generation.wrapping_add(1);
        self.pending = Some(target.clone());
        self.commit = Some(commit);
        self.status = None;
        self.reading = None;
        if !target.valid() {
            self.refuse(
                "The session open target has no recorded owning identity.".into(),
                cx,
            );
            return self.generation;
        }
        if self.saving.is_none() && self.guarding.is_none() && self.migrating.is_none() {
            if target.checkout().is_some() && self.current_key() == target.checkout() {
                if let Some(commit) = self.commit.take() {
                    match commit(&target, window, cx) {
                        Ok(()) => {
                            self.activated(target, true, cx);
                        }
                        Err(error) => self.refuse(error, cx),
                    }
                }
            } else {
                self.begin_transition(window, cx);
            }
        }
        cx.notify();
        self.generation
    }
    /// Cancellation is correlated: an obsolete navigator cannot cancel a newer request.
    pub fn cancel(&mut self, generation: u64, cx: &mut Context<Self>) {
        if self.generation != generation || !self.active_request {
            return;
        }
        self.reading = None;
        self.refuse(
            "The workspace open was cancelled; the current workspace remains open.".into(),
            cx,
        );
    }
    fn refuse(&mut self, reason: String, cx: &mut Context<Self>) {
        if !self.active_request {
            return;
        }
        self.active_request = false;
        self.commit = None;
        self.status = Some(reason.clone());
        let event = WorkspaceRefused {
            generation: self.generation,
            target: self.pending.clone().expect("active request target"),
            reason,
        };
        // Delivery is deferred until request returns its generation to the subscribed navigator.
        let view = cx.entity();
        cx.defer(move |cx| view.update(cx, |_, cx| cx.emit(event)));
        cx.notify();
    }
    fn activated(&mut self, target: WorkspaceTarget, restored: bool, cx: &mut Context<Self>) {
        self.active_request = false;
        self.pending = None;
        self.commit = None;
        self.activated_target = Some(target.clone());
        self.migrated = None;
        let event = WorkspaceActivated {
            generation: self.generation,
            target,
            restored,
        };
        let view = cx.entity();
        cx.defer(move |cx| view.update(cx, |_, cx| cx.emit(event)));
        cx.notify();
    }
    /// Pane owners call this only for a person changing view/draft state, never while restoring.
    pub fn changed(cx: &mut App) {
        if WorkspacePanes::is_restoring(cx) {
            return;
        }
        let Some(key) = WorkspacePanes::key(cx).cloned() else {
            return;
        };
        let Some(view) = Self::try_global(cx) else {
            return;
        };
        cx.defer(move |cx| {
            view.update(cx, |view, cx| {
                if let Some(current) = &mut view.current
                    && current.key == key
                {
                    current.writable = true;
                    cx.notify();
                }
            })
        });
    }
    fn begin_transition(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if !self.active_request {
            return;
        }
        if self.current.is_none() && !self.migrate_legacy(window, cx) {
            return;
        }
        let Some(current) = &self.current else {
            self.read_target(window, cx);
            return;
        };
        // A failed/corrupt hydration is never saved over merely because another session was selected.
        if !current.writable {
            self.read_target(window, cx);
            return;
        }
        let snapshot = match WorkspacePanes::capture(window, cx) {
            Ok(snapshot) => snapshot,
            Err(error) => {
                self.status = Some(format!("Not saved: {error}"));
                self.guard_outgoing(window, cx);
                return;
            }
        };
        if current.safe_to_leave.as_ref() == Some(&snapshot) {
            self.read_target(window, cx);
            return;
        }
        let (key, expected) = (current.key.clone(), current.revision);
        let Some(socket) = SocketClient::try_bulk(cx) else {
            self.refuse("Not saved: the bulk connection is unavailable.".into(), cx);
            return;
        };
        let call = socket.read(cx).call::<Saved>(
            "workspaces",
            "save",
            json!({"machine":key.machine,"sessionId":key.session_id,
            "checkoutRoot":key.checkout_root,"expectedRevision":expected,"snapshot":snapshot}),
            None,
        );
        let handle = window.window_handle();
        self.saving = Some(cx.spawn(async move |view, cx| {
            let outcome = call.await;
            let saved = match outcome {
                Outcome::Ok(saved) if saved.revision == expected + 1 => Ok(saved.revision),
                failed => {
                    // A lost save result is reconciled by exact revision and captured view JSON, never blindly replayed.
                    let message = match failed { Outcome::Failed(error) => error.message, _ => "The workspace save result is unknown.".into() };
                    let read = view.update(cx, |_, cx| socket.read(cx).call::<Read>("workspaces", "read", json!({
                        "machine":key.machine,"sessionId":key.session_id,"checkoutRoot":key.checkout_root}), None));
                    match read { Ok(read) => match read.await {
                        Outcome::Ok(read) if read.revision == expected + 1 && read.snapshot.as_ref() == Some(&snapshot) => Ok(read.revision),
                        _ => Err(message),
                    }, Err(_) => return }
                }
            };
            let _ = handle.update(cx, |_, window, cx| view.update(cx, |view, cx| {
                view.saving = None;
                if view.current_key() != Some(&key) { return; }
                match saved {
                    Ok(revision) => {
                        let current = view.current.as_mut().expect("same outgoing workspace");
                        current.revision = revision; current.safe_to_leave = Some(snapshot);
                        // Edits made while the save awaited its receipt must be saved before releasing their panes.
                        view.begin_transition(window, cx);
                    }
                    Err(error) => { view.refuse(format!("Not saved: {error}"), cx); }
                }
                cx.notify();
            }));
        }));
    }
    fn guard_outgoing(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let Some(current) = &self.current else {
            return;
        };
        if cx
            .try_global::<crate::features::files::document_store::DocumentStore>()
            .is_none_or(|docs| {
                docs.dirty_keys(&current.key.machine, &current.key.checkout_root)
                    .is_empty()
            })
        {
            self.refuse(
                self.status
                    .clone()
                    .unwrap_or_else(|| "The workspace could not be captured.".into()),
                cx,
            );
            return;
        }
        let generation = self.generation;
        let guard = super::drafts_guard::run(current.key.clone(), window, cx);
        let handle = window.window_handle();
        self.guarding = Some(cx.spawn(async move |view, cx| {
            let safe = guard.await;
            let _ = handle.update(cx, |_, window, cx| {
                view.update(cx, |view, cx| {
                    view.guarding = None;
                    if !view.active_request {
                        return;
                    }
                    if safe || generation != view.generation {
                        // A superseded prompt cannot refuse a newer request.
                        // Reducing an oversize capture still requires an actual successful workspace CAS save.
                        view.begin_transition(window, cx);
                    } else {
                        view.refuse(
                            "Not saved: the current workspace and its drafts remain open.".into(),
                            cx,
                        );
                    }
                    cx.notify();
                })
            });
        }));
    }
    fn read_target(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if !self.active_request {
            return;
        }
        let Some(target) = self.pending.clone() else {
            return;
        };
        let WorkspaceTarget::Checkout(key) = target.clone() else {
            self.commit_transcript(target, window, cx);
            return;
        };
        let generation = self.generation;
        if self.current_key() == Some(&key) {
            if let Some(commit) = self.commit.take() {
                match commit(&target, window, cx) {
                    Ok(()) => {
                        self.activated(target, true, cx);
                    }
                    Err(error) => self.refuse(error, cx),
                }
            }
            return;
        }
        let Some(socket) = SocketClient::try_bulk(cx) else {
            self.refuse(
                "Couldn't restore this workspace: the bulk connection is unavailable.".into(),
                cx,
            );
            return;
        };
        let call = socket.read(cx).call::<Read>("workspaces", "read", json!({"machine":key.machine,"sessionId":key.session_id,"checkoutRoot":key.checkout_root}), None);
        let handle = window.window_handle();
        self.reading = Some(cx.spawn(async move |view, cx| {
            let answer = call.await;
            let _ = handle.update(cx, |_, window, cx| view.update(cx, |view, cx| {
                if !view.active_request || generation != view.generation || view.pending.as_ref() != Some(&target) { return; }
                view.reading = None;
                // A person may have edited the outgoing document while this target read was in flight.
                if view.current.as_ref().is_some_and(|current| current.writable
                    && WorkspacePanes::capture(window,cx).ok().as_ref() != current.safe_to_leave.as_ref()) {
                    view.begin_transition(window, cx); return;
                }
                let read = match answer {
                    Outcome::Ok(read) => read,
                    Outcome::Failed(error) => { view.refuse(format!("Couldn't restore this workspace: {}", error.message), cx); return; }
                    Outcome::Aborted => { view.refuse("Couldn't restore this workspace: the connection closed.".into(), cx); return; }
                };
                let empty = json!({"version":1});
                let snapshot = if read.revision == 0 && read.snapshot.is_none() { &empty } else { read.snapshot.as_ref().unwrap_or(&Value::Null) };
                if let Err(error) = WorkspacePanes::validate_snapshot(snapshot, &key, cx) {
                    view.refuse(format!("Couldn't restore this workspace: {error}. The stored snapshot and current view were left untouched."), cx); return;
                }
                let previous = WorkspacePanes::key(cx).cloned();
                let Some(commit) = view.commit.take() else { view.refuse("Open the session again to prepare its navigation.".into(), cx); return; };
                WorkspacePanes::set_key(key.clone(), cx);
                if let Err(error) = commit(&target, window, cx) {
                    if let Some(previous) = previous { WorkspacePanes::set_key(previous, cx); } else { WorkspacePanes::clear_key(cx); }
                    view.refuse(error, cx); return;
                }
                let restored = match WorkspacePanes::restore(snapshot, window, cx) {
                    Ok(())=>true,
                    Err(error)=>{
                        view.status=Some(format!("Couldn't restore this workspace's panes: {error}. The target transcript is visible; its stored workspace snapshot was left untouched."));
                        false
                    }
                };
                view.current = Some(Open { key: key.clone(), revision: read.revision, writable: restored, safe_to_leave: None });
                view.watch(key.clone(), cx);
                view.activated(target, restored, cx);
            }));
        }));
    }
    fn commit_transcript(
        &mut self,
        target: WorkspaceTarget,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let previous = WorkspacePanes::key(cx).cloned();
        let Some(commit) = self.commit.take() else {
            self.refuse("Prepare this transcript open again.".into(), cx);
            return;
        };
        WorkspacePanes::clear_key(cx);
        if let Err(error) = commit(&target, window, cx) {
            if let Some(previous) = previous {
                WorkspacePanes::set_key(previous, cx);
            }
            self.refuse(error, cx);
            return;
        }
        // Only the already validated source-qualified transcript is committed. No fake checkout is hydrated.
        WorkspacePanes::deactivate(window, cx);
        self.current = None;
        self.watching = None;
        self.activated(target, false, cx);
    }
    fn watch(&mut self, key: WorkspaceKey, cx: &mut Context<Self>) {
        self.watching = None;
        let Some(socket) = SocketClient::try_global(cx) else {
            return;
        };
        let topic_key = format!("{}/{}/{}", key.machine, key.session_id, key.checkout_root);
        let mut subscription = socket.read(cx).subscribe("workspaces", Some(&topic_key));
        self.watching = Some(cx.spawn(async move |view, cx| {
            while let Ok(update) = subscription.updates.recv().await {
                let items = match update { Update::State(items) => items, Update::Change { key, item } => vec![(key,item)] };
                let _ = view.update(cx, |view, cx| {
                    if view.current_key() != Some(&key) || view.saving.is_some() { return; }
                    if let Some(current) = &view.current && items.iter().any(|(item_key, item)| item_key == &topic_key
                        && item.get("revision").and_then(Value::as_u64).is_some_and(|revision| revision > current.revision)) {
                        view.status = Some("This workspace changed elsewhere. The live view is preserved; another save requires reconciliation.".into()); cx.notify();
                    }
                });
            }
        }));
    }
}
#[cfg(test)]
#[path = "workspace_coordinator_tests.rs"]
mod tests;

#[cfg(test)]
#[path = "workspace_rootless_tests.rs"]
mod rootless_tests;

#[path = "workspace_migration.rs"]
mod migration;

#[cfg(test)]
#[path = "workspace_restore_tests.rs"]
mod restore_tests;

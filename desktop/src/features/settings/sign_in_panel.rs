//! One common native provider sign-in panel. Safe durable state comes from the owning Engine;
//! sensitive challenge material and owned work disappear when this view is closed.
mod browser;
mod challenge;
mod commands;
mod projection;
mod reads;
mod target;
#[cfg(test)]
mod tests;
mod view;
use crate::core::socket::client::SocketClient;
use crate::core::socket::connection_state::ConnectionState;
use crate::core::socket::engine_subscription::{EnginePage, EngineUpdate};
use crate::core::socket::outcome::Outcome;
use challenge::ChallengeState;
use gpui_kit::component::input::InputState;
use gpui_kit::component::{Placement, WindowExt as _};
use gpui_kit::*;
use projection::Projection;
use serde::Deserialize;
use serde_json::{Value, json};
pub use target::SignInTarget;

pub struct SignedIn;
impl EventEmitter<SignedIn> for SignInPanel {}
#[derive(Clone, Copy, PartialEq, Eq)]
struct ConnectionIdentity {
    main: EntityId,
    main_generation: u64,
    bulk: EntityId,
    bulk_generation: u64,
}
fn connection_identity(cx: &App) -> Option<ConnectionIdentity> {
    let main = SocketClient::try_global(cx)?;
    let bulk = SocketClient::try_bulk(cx)?;
    if !matches!(main.read(cx).state(), ConnectionState::Connected { .. })
        || !matches!(bulk.read(cx).state(), ConnectionState::Connected { .. })
    {
        return None;
    }
    Some(ConnectionIdentity {
        main: main.entity_id(),
        main_generation: main.read(cx).connection_generation(),
        bulk: bulk.entity_id(),
        bulk_generation: bulk.read(cx).connection_generation(),
    })
}
#[derive(Deserialize)]
struct MachineInfo {
    machine: String,
}

pub struct SignInPanel {
    projection: Projection,
    challenge: ChallengeState,
    code: Entity<InputState>,
    window: AnyWindowHandle,
    method_id: Option<String>,
    error: SharedString,
    generation: u64,
    read_revision: u64,
    identity: Option<ConnectionIdentity>,
    announced: Option<String>,
    resnapshot_pending: bool,
    resnapshot: Option<Box<dyn Fn()>>,
    provider_dirty: bool,
    operation_dirty: bool,
    challenge_dirty: bool,
    retry_command: Option<Value>,
    _connections: Vec<Subscription>,
    _verify: Option<Task<()>>,
    _stream: Option<Task<()>>,
    _provider: Option<Task<()>>,
    _operation: Option<Task<()>>,
    _challenge: Option<Task<()>>,
    _command: Option<Task<()>>,
    _opening: Option<Task<()>>,
    _submit: Option<Task<()>>,
}

/// This same view is the entry for the new-session form and later recovery surfaces.
pub fn open(
    target: SignInTarget,
    window: &mut Window,
    cx: &mut App,
) -> Option<Entity<SignInPanel>> {
    if window.has_active_sheet(cx) {
        return None;
    }
    let panel = cx.new(|cx| SignInPanel::new(target, window, cx));
    let view = panel.clone();
    window.open_sheet_at(Placement::Right, cx, move |sheet, _, _| {
        sheet.title("Provider sign in").child(view.clone())
    });
    Some(panel)
}
impl SignInPanel {
    fn new(target: SignInTarget, window: &mut Window, cx: &mut Context<Self>) -> Self {
        let code = cx.new(|cx| {
            InputState::new(window, cx)
                .masked(true)
                .placeholder("Code from the sign-in page")
        });
        let mut subscriptions = vec![];
        for socket in [SocketClient::try_global(cx), SocketClient::try_bulk(cx)]
            .into_iter()
            .flatten()
        {
            subscriptions.push(cx.observe(&socket, |panel: &mut Self, _, cx| panel.connect(cx)));
        }
        let mut panel = Self {
            projection: Projection::new(target),
            challenge: ChallengeState::default(),
            code,
            window: window.window_handle(),
            method_id: None,
            error: Default::default(),
            generation: 0,
            read_revision: 0,
            identity: None,
            announced: None,
            resnapshot_pending: false,
            resnapshot: None,
            provider_dirty: false,
            operation_dirty: false,
            challenge_dirty: false,
            retry_command: None,
            _connections: subscriptions,
            _verify: None,
            _stream: None,
            _provider: None,
            _operation: None,
            _challenge: None,
            _command: None,
            _opening: None,
            _submit: None,
        };
        panel.connect(cx);
        panel
    }
    fn current(&self, generation: u64, cx: &App) -> bool {
        self.generation == generation
            && self.identity.is_some()
            && connection_identity(cx) == self.identity
    }
    fn connect(&mut self, cx: &mut Context<Self>) {
        let identity = connection_identity(cx);
        if self.identity == identity && self.generation > 0 {
            return;
        }
        self.generation += 1;
        self.read_revision += 1;
        self.identity = identity;
        self._verify = None;
        self._stream = None;
        self._provider = None;
        self._operation = None;
        self._challenge = None;
        self._opening = None;
        self._submit = None;
        self._command = None;
        self.resnapshot = None;
        self.challenge.replace_operation("");
        self.projection.ready = false;
        self.provider_dirty = false;
        self.operation_dirty = false;
        self.challenge_dirty = false;
        self.resnapshot_pending = false;
        let Some(_) = identity else {
            self.error = "The backend is disconnected.".into();
            cx.notify();
            return;
        };
        let Some(socket) = SocketClient::try_global(cx) else {
            return;
        };
        self.error = Default::default();
        let generation = self.generation;
        let call = socket
            .read(cx)
            .call::<MachineInfo>("engine", "machine", json!({}), None);
        self._verify=Some(cx.spawn(async move |this,cx| {
            let result=call.await;
            _=this.update(cx,|panel,cx| {
                if !panel.current(generation,cx) { return; }
                panel._verify=None;
                if matches!(result,Outcome::Ok(ref info) if info.machine==panel.projection.target.machine) { panel.subscribe(cx); }
                else { panel.error="The provider's owning machine changed. Close this panel and select the agent again.".into(); }
                cx.notify();
            });
        }));
    }
    fn subscribe(&mut self, cx: &mut Context<Self>) {
        let Some(socket) = SocketClient::try_global(cx) else {
            return;
        };
        let mut subscription = socket
            .read(cx)
            .subscribe_engine("providers", &self.projection.target.machine);
        self.resnapshot = Some(subscription.resnapshot_request());
        let generation = self.generation;
        self._stream = Some(cx.spawn(async move |this, cx| {
            while let Ok(update) = subscription.updates.recv().await {
                let applied = this.update(cx, |panel, cx| {
                    if !panel.current(generation, cx) {
                        return false;
                    }
                    panel.apply_page(&update, cx);
                    true
                });
                if !matches!(applied, Ok(true)) {
                    return;
                }
                subscription.acknowledge(&update);
            }
        }));
    }
    fn apply_page(&mut self, update: &EngineUpdate, cx: &mut Context<Self>) {
        match &update.page {
            EnginePage::Snapshot {
                items, more, reset, ..
            } => {
                if *reset {
                    self.read_revision += 1;
                    self._provider = None;
                    self._operation = None;
                    self._challenge = None;
                    self.projection.begin_snapshot();
                    self.resnapshot_pending = true;
                }
                for item in items {
                    self.projection.snapshot_item(item.clone());
                }
                if !*more {
                    self.projection.finish_snapshot();
                    self.resnapshot_pending = false;
                    self.provider_dirty = true;
                    self.operation_dirty = true;
                    self.after_operation(cx);
                    self.read_provider(cx);
                    self.read_operation(cx);
                }
            }
            EnginePage::Events { events, .. } => {
                for event in events {
                    if event.kind == "provider.changed"
                        && event.entity_id == self.projection.target.provider_target_id
                    {
                        self.provider_dirty = true;
                    }
                    if event.kind == "sign_in.changed" {
                        if self.projection.operation_id.as_deref() == Some(&event.entity_id) {
                            self.operation_dirty = true;
                            self.challenge_dirty = true;
                        } else {
                            self.request_snapshot();
                        }
                    }
                }
                self.read_provider(cx);
                self.read_operation(cx);
                self.read_challenge(cx);
            }
            EnginePage::Unavailable => {
                self.projection.ready = false;
                self.error =
                    "Provider updates are unavailable. Close this panel and try again.".into();
            }
        }
        cx.notify();
    }
    fn request_snapshot(&mut self) {
        if self.resnapshot_pending {
            return;
        }
        if let Some(request) = &self.resnapshot {
            self.resnapshot_pending = true;
            self.projection.ready = false;
            request();
        }
    }
}

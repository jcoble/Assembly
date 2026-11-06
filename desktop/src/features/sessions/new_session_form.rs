//! One project chooser and Composer draft. The first send owns a stable create request;
//! the initial prompt waits for the accepted checkout, idle state and guarded session navigation.

use gpui_kit::*;
use serde_json::json;
use std::rc::Rc;

use crate::core::state::machines::Machines;
use crate::features::session::client::session_client::{CallError, SessionClient};
use crate::features::session::client::socket_session_client::SocketSessionClient;
use crate::features::session::composer::agent_config::{AgentList, AgentTarget};
use crate::features::session::composer::draft_config::{self, Catalogue, DraftConfig};
use crate::features::settings::sign_in_panel::{self, SignInTarget};

pub struct NewSessionForm {
    /// Drawn as the Chat pane's draft, not in a dialog.
    centered: bool,
    chooser_open: bool,
    chooser_focus: FocusHandle,
    project_chooser: Entity<crate::features::projects::project_chooser::ProjectChooser>,
    start_composer: Entity<crate::features::session::composer::composer_view::Composer>,
    _project_choice: Subscription,
    _start_requested: Subscription,
    location_source: Option<Rc<start_source::StartSource>>,
    inspection: Option<crate::features::projects::new_project::FolderInspection>,
    location_read: Option<Task<()>>,
    location_generation: u64,
    attempt: Option<first_send::StartAttempt>,
    selected_project: Option<crate::features::projects::projects::ProjectRecord>,
    _project_created: Option<Subscription>,
    /// `None` for this computer.
    machine: Option<String>,
    agent: Option<String>,
    /// The chosen machine's agents, as (id, name).
    agents: Vec<AgentTarget>,
    auth_machine: Option<String>,
    auth_error: SharedString,
    _auth_machine: Option<Task<()>>,
    _sign_in: Option<Subscription>,
    client: Rc<dyn SessionClient>,
    config: Option<DraftConfig>,
    generation: u64,
    picks: u64,
    list_generation: u64,
    discovering: bool,
    _discover: Option<Task<()>>,
    route: String,
    backend_connected: bool,
    backend_generation: Option<u64>,
    _connection: Option<Subscription>,
    _bulk_connection: Option<Subscription>,
    _preferences: Option<Subscription>,
    cwd: Option<String>,
    draft_window: AnyWindowHandle,
    /// Why the last load or create failed, or what's missing.
    error: SharedString,
    /// An agents load or a create is on its way: the agents load again when the machines change while neither is,
    /// and Start session waits for the create.
    loading: bool,
    starting: bool,
    /// The connection dropped the last agents load, so the next machines change (a reconnect) loads them again.
    retry_agents: bool,
    _load: Option<Task<()>>,
    _create: Option<Task<()>>,
    _machines: Option<Subscription>,
}

/// The form is done: the session it made is selected.
pub struct Created;

impl EventEmitter<Created> for NewSessionForm {}

/// What a draft in the Chat pane asks of whoever holds it.
pub enum DraftEvent {
    Discard,
    /// Its start failed, so it has to show again.
    Failed,
}

impl EventEmitter<DraftEvent> for NewSessionForm {}

impl NewSessionForm {
    pub fn new(window: &mut Window, cx: &mut Context<Self>) -> Self {
        Self::with_client(Rc::new(SocketSessionClient), window, cx)
    }

    fn pick_machine(&mut self, machine: Option<String>, cx: &mut Context<Self>) {
        if self.starting {
            return;
        }
        if self.attempt.is_some() {
            self.start_error("The original start keeps its project and machine. Reconcile it before making another request.",cx);
            return;
        }
        if self.machine != machine {
            self.selected_project = None;
            self.location_source = None;
            self.inspection = None;
            self.cwd = None;
            (self.machine, self.agent, self.agents) = (machine, None, Vec::new());
            self.error = SharedString::default();
            self.route = self.machine_route(cx);
            self.reload_location(cx);
            self.discover(cx);
            self.load_agents(cx);
            cx.notify();
        }
    }

    /// The selected machine advertises opaque target IDs; no provider/account identity is inferred.
    fn load_agents(&mut self, cx: &mut Context<Self>) {
        if !self.backend_connected || self.backend_generation.is_none() {
            self._load = None;
            self.loading = false;
            self.retry_agents = true;
            cx.notify();
            return;
        }
        self.list_generation += 1;
        self.auth_machine = None;
        self._auth_machine = None;
        self._sign_in = None;
        self.auth_error = Default::default();
        let generation = self.list_generation;
        let machine = self.machine.clone();
        let call = self
            .client
            .call("machines", "agents", json!({"machine": machine}), None, cx);
        (self.loading, self.retry_agents) = (true, false);
        self._load = Some(cx.spawn(async move |form, cx| {
            let answer = call.await.and_then(|value| {
                serde_json::from_value::<AgentList>(value)
                    .map_err(|e| CallError::Failed(e.to_string()))
            });
            _ = form.update(cx, |form, cx| {
                if form.list_generation != generation || form.machine != machine {
                    return;
                }
                form.loading = false;
                match answer {
                    Ok(list) => {
                        let selected = form.agent.clone();
                        form.agents = list.agents;
                        form.agent = selected
                            .filter(|id| form.agents.iter().any(|a| a.id == *id))
                            .or_else(|| form.agents.first().map(|a| a.id.clone()));
                        form.error = SharedString::default();
                        form.load_auth_machine(cx);
                        form.discover(cx);
                    }
                    Err(CallError::Aborted) => {
                        form.retry_agents = true;
                        form.error =
                            "The connection dropped; the agents load again once it's back.".into();
                    }
                    Err(error) => {
                        form.error = format!("Couldn't list the agents: {}", error.reason()).into()
                    }
                }
                cx.notify();
            });
        }));
    }

    fn observe_connection(
        &mut self,
        connected: bool,
        generation: Option<u64>,
        cx: &mut Context<Self>,
    ) {
        let connected = connected && generation.is_some();
        let generation = if connected { generation } else { None };
        if connected == self.backend_connected && generation == self.backend_generation {
            return;
        }
        self.backend_connected = connected;
        self.backend_generation = generation;
        if self.attempt.is_some() {
            self._load = None;
            self._discover = None;
            self.start_error("The original start connection changed. Its request, settings and draft were retained.",cx);
            return;
        }
        self.list_generation += 1;
        self._load = None;
        self.loading = false;
        self.agents.clear();
        if !connected {
            self.agent = None;
        }
        self.auth_machine = None;
        self._auth_machine = None;
        self._sign_in = None;
        self.location_source = None;
        self.inspection = None;
        self.cwd = None;
        self.discover(cx);
        if connected {
            self.load_agents(cx);
            self.reload_location(cx);
        }
    }

    fn load_auth_machine(&mut self, cx: &mut Context<Self>) {
        if !self.backend_connected || self.backend_generation.is_none() {
            return;
        }
        if self.machine.is_some() {
            return;
        }
        if !self.agents.iter().any(|agent| {
            agent.account_context.is_some()
                && agent.provider_target_id.is_some()
                && agent.target_id.is_some()
        }) {
            if self.agent.is_some() {
                self.auth_error="Sign in is unavailable: this backend did not advertise a complete provider identity.".into();
            }
            return;
        }
        let generation = self.list_generation;
        let backend_generation = self.backend_generation;
        let call = self.client.call("engine", "machine", json!({}), None, cx);
        self._auth_machine=Some(cx.spawn(async move |form,cx| {
            let result=call.await;
            _=form.update(cx,|form,cx| {
                if form.list_generation!=generation || form.backend_generation!=backend_generation || form.machine.is_some() || !form.backend_connected { return; }
                form._auth_machine=None;
                match result {
                    Ok(value)=>{
                        form.auth_machine=value["machine"].as_str().filter(|id|!id.is_empty()).map(String::from);
                        if form.auth_machine.is_none() { form.auth_error="Sign in is unavailable: the backend did not supply its owning machine identity.".into(); }
                        else if form.sign_in_target().is_none() { form.auth_error="Sign in is unavailable: the advertised provider identity did not match.".into(); }
                    }
                    _=>form.auth_error="Couldn't read the provider's owning machine. Select the agent again after reconnecting.".into(),
                }
                cx.notify();
            });
        }));
    }
    fn pick_agent(&mut self, id: &str, cx: &mut Context<Self>) {
        if self.attempt.is_some() || self.starting || self.agent.as_deref() == Some(id) {
            return;
        }
        self.agent = Some(id.into());
        if self.auth_machine.is_some() {
            self.auth_error = if self.sign_in_target().is_some() {
                Default::default()
            } else {
                "Sign in is unavailable: the advertised provider identity did not match.".into()
            };
        }
        self.discover(cx);
    }

    fn sign_in_target(&self) -> Option<SignInTarget> {
        if self.machine.is_some() || self.loading || !self.backend_connected {
            return None;
        }
        let agent = self
            .agents
            .iter()
            .find(|agent| Some(&agent.id) == self.agent.as_ref())?;
        SignInTarget::local(self.auth_machine.as_deref()?, "This Mac", agent)
    }
    fn open_sign_in(
        &mut self,
        expected: SignInTarget,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if self.sign_in_target().as_ref() != Some(&expected) {
            return;
        }
        let Some(panel) = sign_in_panel::open(expected.clone(), window, cx) else {
            return;
        };
        let revision = self.list_generation;
        self._sign_in = Some(cx.subscribe(
            &panel,
            move |form, _, _: &sign_in_panel::SignedIn, cx| {
                form.signed_in(&expected, revision, cx);
            },
        ));
    }

    fn signed_in(&mut self, expected: &SignInTarget, revision: u64, cx: &mut Context<Self>) {
        if self.list_generation == revision
            && self.sign_in_target().as_ref() == Some(expected)
            && !self.starting
            && self.attempt.is_none()
            && self.config.is_none()
        {
            self.discover(cx);
        }
    }

    fn machine_route(&self, cx: &App) -> String {
        Machines::try_global(cx)
            .and_then(|machines| {
                machines
                    .read(cx)
                    .machines()
                    .find(|item| item.name == self.machine)
                    .map(|item| {
                        format!(
                            "{:?}",
                            (&item.ssh, item.port, &item.token_file, item.reachable)
                        )
                    })
            })
            .unwrap_or_default()
    }

    fn selected_target(&self) -> Option<&str> {
        self.agents
            .iter()
            .find(|a| Some(&a.id) == self.agent.as_ref())
            .and_then(|a| a.target_id.as_deref())
            .filter(|id| !id.is_empty())
    }

    fn discover(&mut self, cx: &mut Context<Self>) {
        self.generation += 1;
        self.picks = 0;
        self._discover = None;
        self.config = None;
        self.discovering = false;
        self.error = SharedString::default();
        self.sync_start_state(cx);
        if !self.backend_connected || Machines::offline(self.machine.as_deref(), cx) {
            self.error = "Agent settings unavailable: the selected machine is disconnected.".into();
            cx.notify();
            return;
        }
        let cwd = self.cwd.clone().unwrap_or_default();
        if !std::path::Path::new(&cwd).is_absolute() || self.agent.is_none() {
            cx.notify();
            return;
        }
        let Some(target) = self.selected_target().map(String::from) else {
            self.error =
                "Agent settings unavailable: this backend did not advertise a composer target."
                    .into();
            cx.notify();
            return;
        };
        let agent = self.agent.clone().unwrap();
        let machine = self.machine.clone();
        let (generation, picks) = (self.generation, self.picks);
        let args = json!({"agent":agent,"targetId":target,"cwd":cwd,"machine":machine});
        let call = self.client.call("config", "discover", args, None, cx);
        self.discovering = true;
        self._discover = Some(cx.spawn(async move |form, cx| {
            let answer = call.await;
            _ = form.update(cx, |form, cx| {
                if form.generation != generation
                    || form.picks != picks
                    || form.machine != machine
                    || form.agent.as_deref() != Some(&agent)
                    || form.selected_target() != Some(target.as_str())
                    || form.cwd.as_deref() != Some(cwd.as_str())
                {
                    return;
                }
                form.discovering = false;
                let remembered = draft_config::remembered(&agent, cx);
                let result = answer
                    .map_err(|e| e.reason())
                    .and_then(|v| serde_json::from_value::<Catalogue>(v).map_err(|e| e.to_string()))
                    .and_then(|catalogue| DraftConfig::new(catalogue, remembered.as_ref()));
                match result {
                    Ok(config) => form.config = Some(config),
                    Err(reason) => {
                        form.error = format!("Agent settings unavailable: {reason}").into()
                    }
                }
                form.sync_start_state(cx);
                cx.notify();
            });
        }));
        cx.notify();
    }

    fn choose(&mut self, id: &str, value: &str, cx: &mut Context<Self>) {
        if self.starting || self.attempt.is_some() {
            return;
        }
        let remembered = self
            .agent
            .as_deref()
            .and_then(|agent| draft_config::remembered(agent, cx));
        let Some(config) = self.config.as_mut() else {
            return;
        };
        match config.choose(
            id,
            value,
            remembered.as_ref().and_then(|v| v["effort"].as_str()),
        ) {
            Ok(()) => {
                self.picks += 1;
                self.error = SharedString::default();
                if let Some(agent) = &self.agent {
                    draft_config::remember(agent, &config.config, cx);
                }
            }
            Err(reason) => self.error = format!("Agent settings unavailable: {reason}").into(),
        }
        self.sync_start_state(cx);
        cx.notify();
    }
}

#[path = "new_session_form/build.rs"]
mod build;
#[path = "new_session_form/checkout_wait.rs"]
mod checkout_wait;
#[path = "new_session_form/create_reply.rs"]
mod create_reply;
#[path = "new_session_form/first_send.rs"]
mod first_send;
#[path = "new_session_form/location.rs"]
mod location;
#[path = "new_session_form/project.rs"]
mod project;
#[path = "new_session_form/start_request.rs"]
mod start_request;
#[path = "new_session_form/start_slots.rs"]
mod start_slots;
#[path = "new_session_form/start_source.rs"]
mod start_source;
#[path = "new_session_form/view.rs"]
mod view;

#[cfg(test)]
#[path = "new_session_form_tests.rs"]
mod tests;

#[cfg(test)]
#[path = "new_session_form_real_tests.rs"]
mod real_tests;

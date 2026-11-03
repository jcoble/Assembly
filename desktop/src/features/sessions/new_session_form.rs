//! `NewSessionForm` starts a session: the machine (from the machines list) and its agent (`machines/agents`), and the
//! folder, then `sessions/create`. The new session is selected and opens empty, ready for its first message.
//! Replaces the new session form in routes/+page.svelte.

use gpui_kit::component::input::{InputEvent, TextareaState};
use gpui_kit::*;
use serde::Deserialize;
use serde_json::{Value, json};
use std::rc::Rc;

use crate::core::state::machines::Machines;
use crate::core::state::selected_session::SelectedSession;
use crate::features::session::client::session_client::{CallError, SessionClient};
use crate::features::session::client::socket_session_client::SocketSessionClient;
use crate::features::session::composer::agent_config::{AgentList, AgentTarget};
use crate::features::session::composer::draft_config::{self, Catalogue, DraftConfig};
use crate::features::settings::sign_in_panel::{self, SignInTarget};

pub struct NewSessionForm {
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
    _folder: Subscription,
    route: String,
    backend_connected: bool,
    backend_generation: Option<u64>,
    _connection: Option<Subscription>,
    _preferences: Option<Subscription>,
    folder: Entity<TextareaState>,
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

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Made {
    session_id: String,
}

/// `sessions/create`'s args, or `None` while the agent or folder is missing.
pub fn create_args(agent: Option<&str>, folder: &str, machine: Option<&str>) -> Option<Value> {
    let (agent, folder) = (agent?, folder.trim());
    if folder.is_empty() {
        return None;
    }
    let mut args = json!({ "agent": agent, "repoUrl": folder });
    if let Some(machine) = machine {
        args["machine"] = json!(machine);
    }
    Some(args)
}

impl NewSessionForm {
    pub fn new(window: &mut Window, cx: &mut Context<Self>) -> Self {
        Self::with_client(Rc::new(SocketSessionClient), window, cx)
    }

    fn with_client(
        client: Rc<dyn SessionClient>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Self {
        let folder = cx.new(|cx| {
            TextareaState::new(window, cx)
                .auto_grow(1, 2)
                .placeholder("Folder or https://github.com/owner/repo")
        });
        let folder_changed = cx.subscribe(&folder, |form: &mut Self, _, event, cx| {
            if matches!(event, InputEvent::Change) && !form.starting {
                form.discover(cx);
            }
        });
        // An agents load the connection dropped is made again once the machines list changes (after a reconnect).
        let machines = Machines::try_global(cx).map(|machines| {
            cx.observe(&machines, |form: &mut Self, _, cx| {
                let route = form.machine_route(cx);
                if form.route != route {
                    form.route = route;
                    form.agents.clear();
                    form.auth_machine = None;
                    form._auth_machine = None;
                    form.discover(cx);
                    form.load_agents(cx);
                } else if form.retry_agents && !form.loading {
                    form.load_agents(cx);
                }
                cx.notify();
            })
        });
        let backend_connected = crate::core::socket::client::SocketClient::try_global(cx)
            .map(|socket| {
                matches!(
                    socket.read(cx).state(),
                    crate::core::socket::connection_state::ConnectionState::Connected { .. }
                )
            })
            .unwrap_or(false);
        let backend_generation = crate::core::socket::client::SocketClient::try_global(cx)
            .and_then(|socket| socket.read(cx).connection_generation())
            .filter(|_| backend_connected);
        let backend_connected = backend_connected && backend_generation.is_some();
        let connection = crate::core::socket::client::SocketClient::try_global(cx).map(|socket| {
            cx.observe(&socket, |form: &mut Self, socket, cx| {
                let socket = socket.read(cx);
                let connected = matches!(
                    socket.state(),
                    crate::core::socket::connection_state::ConnectionState::Connected { .. }
                );
                let generation = socket.connection_generation();
                form.observe_connection(connected, generation, cx);
            })
        });
        let preferences =
            crate::core::state::preferences::Preferences::try_global(cx).map(|preferences| {
                cx.observe(&preferences, |form: &mut Self, _, cx| {
                    if form.starting || form.picks != 0 {
                        return;
                    }
                    let remembered = form
                        .agent
                        .as_deref()
                        .and_then(|agent| draft_config::remembered(agent, cx));
                    if let Some(config) = form.config.as_mut()
                        && config.apply_remembered(remembered.as_ref()).is_ok()
                    {
                        cx.notify();
                    }
                })
            });
        let mut form = NewSessionForm {
            machine: None,
            agent: None,
            agents: Vec::new(),
            auth_machine: None,
            auth_error: Default::default(),
            _auth_machine: None,
            _sign_in: None,
            client,
            config: None,
            generation: 0,
            picks: 0,
            list_generation: 0,
            route: String::new(),
            backend_connected,
            backend_generation,
            _connection: connection,
            _preferences: preferences,
            discovering: false,
            _discover: None,
            _folder: folder_changed,
            folder,
            error: SharedString::default(),
            loading: false,
            starting: false,
            retry_agents: false,
            _load: None,
            _create: None,
            _machines: machines,
        };
        form.load_agents(cx);
        form
    }

    /// Fills in a project's folder and machine, for its header's New session.
    pub fn fill(
        &mut self,
        folder: &str,
        machine: Option<String>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if self.starting {
            return;
        }
        let folder = folder.to_string();
        self.folder
            .update(cx, |input, cx| input.set_value(folder, window, cx));
        self.pick_machine(machine, cx);
        self.discover(cx);
    }

    fn pick_machine(&mut self, machine: Option<String>, cx: &mut Context<Self>) {
        if self.starting {
            return;
        }
        if self.machine != machine {
            (self.machine, self.agent, self.agents) = (machine, None, Vec::new());
            self.error = SharedString::default();
            self.route = self.machine_route(cx);
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
        self.discover(cx);
        if connected {
            self.load_agents(cx);
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
        if self.starting || self.agent.as_deref() == Some(id) {
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
        if !self.backend_connected || Machines::offline(self.machine.as_deref(), cx) {
            self.error = "Agent settings unavailable: the selected machine is disconnected.".into();
            cx.notify();
            return;
        }
        let cwd = self.folder.read(cx).value().trim().to_string();
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
                    || form.folder.read(cx).value().trim() != cwd
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
                cx.notify();
            });
        }));
        cx.notify();
    }

    fn choose(&mut self, id: &str, value: &str, cx: &mut Context<Self>) {
        if self.starting {
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
        cx.notify();
    }

    fn create(&mut self, cx: &mut Context<Self>) {
        if self.starting {
            return;
        }
        let folder = self.folder.read(cx).value().to_string();
        let Some(mut args) = create_args(self.agent.as_deref(), &folder, self.machine.as_deref())
        else {
            self.error = "Pick an agent and type a folder.".into();
            cx.notify();
            return;
        };
        if std::path::Path::new(folder.trim()).is_absolute() {
            let (Some(config), Some(target)) = (&self.config, self.selected_target()) else {
                self.error = if self.discovering {
                    "Wait for this machine's agent settings."
                } else {
                    "Agent settings unavailable: choose a ready provider and folder."
                }
                .into();
                cx.notify();
                return;
            };
            if !config.ready() {
                self.error = "Choose a model offered by this provider before starting.".into();
                cx.notify();
                return;
            }
            args["workingFolder"] = json!(folder.trim());
            args["initialConfig"] = json!({"targetId":target,"choices":config.config.choices});
        }
        let call = self.client.call("sessions", "create", args, None, cx);
        (self.starting, self.error) = (true, SharedString::default());
        cx.notify();
        self._create = Some(cx.spawn(async move |form, cx| {
            let outcome = call.await;
            form.update(cx, |form, cx| {
                form.starting = false;
                match outcome {
                    Ok(value) => {
                        let made = match serde_json::from_value::<Made>(value) {
                            Ok(made) => made,
                            Err(error) => {
                                form.error =
                                    format!("Couldn't read the created session: {error}").into();
                                cx.notify();
                                return;
                            }
                        };
                        SelectedSession::global(cx).update(cx, |selected, cx| {
                            selected.select(Some(made.session_id), cx)
                        });
                        cx.emit(Created);
                    }
                    Err(error @ CallError::Uncertain { .. }) => form.error = error.reason().into(),
                    Err(CallError::Aborted | CallError::Unreachable { .. }) => {
                        form.error = "The connection dropped; the session may have started.".into()
                    }
                    Err(error) => {
                        form.error =
                            format!("Couldn't start the session: {}", error.reason()).into()
                    }
                }
                cx.notify();
            })
            .ok();
        }));
    }
}

#[path = "new_session_form/view.rs"]
mod view;

#[cfg(test)]
#[path = "new_session_form_tests.rs"]
mod tests;

#[cfg(test)]
#[path = "new_session_form_real_tests.rs"]
mod real_tests;

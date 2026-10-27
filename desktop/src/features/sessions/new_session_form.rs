//! `NewSessionForm` starts a session: the machine (from the machines list) and its agent (`machines/agents`), and the
//! folder, then `sessions/create`. The new session is selected and opens empty, ready for its first message.
//! Replaces the new session form in routes/+page.svelte.

use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::input::{Textarea, TextareaState};
use gpui_kit::component::{ActiveTheme as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use serde::Deserialize;
use serde_json::{Value, json};

use crate::core::socket::client::SocketClient;
use crate::core::socket::outcome::Outcome;
use crate::core::state::machines::Machines;
use crate::core::state::selected_session::SelectedSession;

pub struct NewSessionForm {
    /// `None` for this computer.
    machine: Option<String>,
    agent: Option<String>,
    /// The chosen machine's agents, as (id, name).
    agents: Vec<(String, String)>,
    folder: Entity<TextareaState>,
    /// Why the last load or create failed.
    error: SharedString,
    _load: Option<Task<()>>,
    _create: Option<Task<()>>,
    _machines: Option<Subscription>,
}

/// The form is done: the session it made is selected.
pub struct Created;

impl EventEmitter<Created> for NewSessionForm {}

#[derive(Deserialize)]
struct AgentList {
    agents: Vec<Agent>,
}

#[derive(Deserialize)]
struct Agent {
    id: String,
    name: String,
}

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
        let folder = cx.new(|cx| {
            TextareaState::new(window, cx)
                .auto_grow(1, 2)
                .placeholder("Folder or https://github.com/owner/repo")
        });
        let machines =
            Machines::try_global(cx).map(|machines| cx.observe(&machines, |_, _, cx| cx.notify()));
        let mut form = NewSessionForm {
            machine: None,
            agent: None,
            agents: Vec::new(),
            folder,
            error: SharedString::default(),
            _load: None,
            _create: None,
            _machines: machines,
        };
        form.load_agents(cx);
        form
    }

    fn pick_machine(&mut self, machine: Option<String>, cx: &mut Context<Self>) {
        if self.machine != machine {
            (self.machine, self.agent, self.agents) = (machine, None, Vec::new());
            self.load_agents(cx);
        }
    }

    /// The chosen machine's agents; the first is picked.
    fn load_agents(&mut self, cx: &mut Context<Self>) {
        let Some(socket) = SocketClient::try_global(cx) else {
            self.error = "The backend isn't running.".into();
            return;
        };
        let args = json!({ "machine": self.machine });
        let call =
            socket
                .read(cx)
                .call::<AgentList>("machines", "agents", args, Some("machines/agents"));
        self._load = Some(cx.spawn(async move |form, cx| {
            let outcome = call.await;
            form.update(cx, |form, cx| {
                match outcome {
                    Outcome::Ok(list) => {
                        form.agents = list
                            .agents
                            .into_iter()
                            .map(|agent| (agent.id, agent.name))
                            .collect();
                        form.agent = form.agents.first().map(|(id, _)| id.clone());
                        form.error = SharedString::default();
                    }
                    Outcome::Failed(failure) => {
                        form.error = format!("Couldn't list the agents: {}", failure.message).into()
                    }
                    Outcome::Aborted => {}
                }
                cx.notify();
            })
            .ok();
        }));
    }

    fn create(&mut self, cx: &mut Context<Self>) {
        let folder = self.folder.read(cx).value().to_string();
        let Some(args) = create_args(self.agent.as_deref(), &folder, self.machine.as_deref())
        else {
            return;
        };
        let Some(socket) = SocketClient::try_global(cx) else {
            return;
        };
        let call = socket
            .read(cx)
            .call::<Made>("sessions", "create", args, None);
        self._create = Some(cx.spawn(async move |form, cx| {
            let outcome = call.await;
            form.update(cx, |form, cx| match outcome {
                Outcome::Ok(made) => {
                    SelectedSession::global(cx).update(cx, |selected, cx| {
                        selected.select(Some(made.session_id), cx)
                    });
                    cx.emit(Created);
                }
                Outcome::Failed(failure) => {
                    form.error = format!("Couldn't start the session: {}", failure.message).into();
                    cx.notify();
                }
                Outcome::Aborted => {}
            })
            .ok();
        }));
    }
}

impl Render for NewSessionForm {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let theme = cx.theme();
        let muted = theme.muted_foreground;
        let machines: Vec<Option<String>> = Machines::try_global(cx)
            .map(|machines| {
                machines
                    .read(cx)
                    .machines()
                    .map(|machine| machine.name.clone())
                    .collect()
            })
            .unwrap_or_default();
        let mut places = h_flex().gap_1().flex_wrap();
        // This computer always, then each remote machine.
        for machine in std::iter::once(None).chain(machines.into_iter().filter(Option::is_some)) {
            let label = machine
                .clone()
                .unwrap_or_else(|| "This computer".to_string());
            let picked = self.machine == machine;
            let id = format!("new:machine:{label}");
            places = places.child(
                Button::new(SharedString::from(id))
                    .compact()
                    .when(picked, |button| button.primary())
                    .when(!picked, |button| button.ghost())
                    .label(label)
                    .on_click(
                        cx.listener(move |form, _, _, cx| form.pick_machine(machine.clone(), cx)),
                    ),
            );
        }
        let mut agents = h_flex().gap_1().flex_wrap();
        for (id, name) in self.agents.clone() {
            let picked = self.agent.as_deref() == Some(&id);
            agents = agents.child(
                Button::new(SharedString::from(format!("new:agent:{id}")))
                    .compact()
                    .when(picked, |button| button.primary())
                    .when(!picked, |button| button.ghost())
                    .label(name)
                    .on_click(cx.listener(move |form, _, _, cx| {
                        form.agent = Some(id.clone());
                        cx.notify();
                    })),
            );
        }
        v_flex()
            .gap_2()
            .p_3()
            .border_b_1()
            .border_color(theme.border)
            .child(div().text_xs().text_color(muted).child("Machine"))
            .child(places)
            .child(div().text_xs().text_color(muted).child("Agent"))
            .child(agents)
            .child(Textarea::new(&self.folder))
            .when(!self.error.is_empty(), |form| {
                form.child(
                    div()
                        .text_xs()
                        .text_color(theme.danger)
                        .child(self.error.clone()),
                )
            })
            .child(
                Button::new("new:create")
                    .primary()
                    .compact()
                    .label("Start session")
                    .on_click(cx.listener(|form, _, _, cx| form.create(cx))),
            )
    }
}

#[cfg(test)]
#[path = "new_session_form_tests.rs"]
mod tests;

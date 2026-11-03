//! `DeployForm` is the dialog's state: a local bundle read by `inspect`, the saved machine and its actual Engine, and one
//! install followed by its push topic. The subscription exists before `start` goes out, a reconnect's fresh state reads
//! the job's status once, and nothing polls. The install belongs to the backend, so closing the dialog leaves it running;
//! reopening finds it again through `LastDeploy`. An answer the form can't trust leaves the outcome unknown, never success
//! or running, and a new deploy waits until that's resolved or the owner forgets it.

use gpui_kit::component::input::{InputEvent, InputState};
use gpui_kit::*;
use serde_json::{Value, json};
use uuid::Uuid;

use super::job::{BundleFacts, Expected, Job, refusal_words};
use crate::core::socket::call_failure::CallFailure;
use crate::core::socket::client::SocketClient;
use crate::core::socket::error_code::ErrorCode;
use crate::core::socket::outcome::Outcome;
use crate::core::socket::update::Update;
use crate::core::state::machines::Machines;

const FEATURE: &str = "remoteInstall";

/// The last install this app started, so a reopened dialog can find it. In memory: an app restart forgets it.
#[derive(Default)]
struct LastDeploy(Option<Expected>);

impl Global for LastDeploy {}

/// The Engine a saved machine answers as now, and which connection said so.
#[derive(Clone, Debug, PartialEq)]
pub(super) struct Target {
    pub engine: String,
    generation: u64,
}

pub struct DeployForm {
    pub(super) path: Entity<InputState>,
    /// What `inspect` read, for the path it read, on the connection it read it on.
    pub(super) facts: Option<(BundleFacts, String, u64)>,
    pub(super) inspecting: bool,
    pub(super) inspect_error: Option<SharedString>,
    _inspect: Option<Task<()>>,
    _choose: Option<Task<()>>,
    pub(super) machine: Option<String>,
    pub(super) target: Option<Target>,
    pub(super) resolving: bool,
    pub(super) target_error: Option<SharedString>,
    _resolve: Option<Task<()>>,
    pub(super) expected: Option<Expected>,
    pub(super) job: Option<Job>,
    /// The form has lost sight of the install; `Some` says why.
    pub(super) unknown: Option<SharedString>,
    pub(super) start_error: Option<SharedString>,
    pub(super) cancel_error: Option<SharedString>,
    pub(super) cancelling: bool,
    _cancel: Option<Task<()>>,
    _watch: Option<Task<()>>,
    generation: Option<u64>,
    _socket: Option<Subscription>,
    _machines: Option<Subscription>,
    _path: Subscription,
}

impl DeployForm {
    pub fn new(window: &mut Window, cx: &mut Context<Self>) -> Self {
        let path = cx.new(|cx| {
            InputState::new(window, cx).placeholder("Path to a .tar.gz bundle on this computer")
        });
        let path_heard = cx.subscribe(&path, |form: &mut Self, _, event: &InputEvent, cx| {
            if matches!(event, InputEvent::Change) {
                form.path_changed(cx);
            }
        });
        let socket = SocketClient::try_global(cx);
        let generation = socket
            .as_ref()
            .and_then(|socket| socket.read(cx).connection_generation());
        let socket_heard = socket.map(|socket| {
            cx.observe(&socket, |form: &mut Self, _, cx| {
                form.connection_changed(cx)
            })
        });
        let machines = Machines::try_global(cx).map(|machines| {
            cx.observe(&machines, |form: &mut Self, _, cx| {
                form.machines_changed(cx)
            })
        });
        let mut form = Self {
            path,
            facts: None,
            inspecting: false,
            inspect_error: None,
            _inspect: None,
            _choose: None,
            machine: None,
            target: None,
            resolving: false,
            target_error: None,
            _resolve: None,
            expected: None,
            job: None,
            unknown: None,
            start_error: None,
            cancel_error: None,
            cancelling: false,
            _cancel: None,
            _watch: None,
            generation,
            _socket: socket_heard,
            _machines: machines,
            _path: path_heard,
        };
        if let Some(expected) = cx.default_global::<LastDeploy>().0.clone() {
            form.machine = Some(expected.machine.clone());
            form.expected = Some(expected);
            form.watch(None, cx);
        } else {
            form.machines_changed(cx);
        }
        form
    }

    fn path_changed(&mut self, cx: &mut Context<Self>) {
        self.facts = None;
        self.inspect_error = None;
        self._inspect = None;
        self.inspecting = false;
        cx.notify();
    }

    pub(super) fn path_text(&self, cx: &App) -> String {
        self.path.read(cx).value().trim().to_string()
    }

    pub(super) fn choose_file(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let picked = cx.prompt_for_paths(PathPromptOptions {
            files: true,
            directories: false,
            multiple: false,
            prompt: Some("Choose".into()),
        });
        self._choose = Some(cx.spawn_in(window, async move |form, cx| {
            let Ok(Ok(Some(paths))) = picked.await else {
                return;
            };
            let Some(path) = paths.first() else {
                return;
            };
            let path = path.to_string_lossy().into_owned();
            _ = form.update_in(cx, |form, window, cx| {
                form.path
                    .update(cx, |input, cx| input.set_value(path, window, cx));
                form.inspect(cx);
            });
        }));
    }

    pub(super) fn inspect(&mut self, cx: &mut Context<Self>) {
        let bundle = self.path_text(cx);
        if bundle.is_empty() {
            return;
        }
        let Some(socket) = SocketClient::try_global(cx) else {
            self.inspect_error = Some("The backend isn't connected.".into());
            cx.notify();
            return;
        };
        let generation = socket.read(cx).connection_generation();
        let call = socket.read(cx).call::<BundleFacts>(
            FEATURE,
            "inspect",
            json!({"bundleFile": bundle}),
            Some("remoteInstall:inspect"),
        );
        (self.inspecting, self.facts, self.inspect_error) = (true, None, None);
        self._inspect = Some(cx.spawn(async move |form, cx| {
            let answer = call.await;
            _ = form.update(cx, |form, cx| {
                form.inspecting = false;
                if form.path_text(cx) != bundle || form.current_generation(cx) != generation {
                    return;
                }
                match (answer, generation) {
                    (Outcome::Ok(facts), Some(generation)) => {
                        form.facts = Some((facts, bundle, generation))
                    }
                    (Outcome::Failed(error), _) => form.inspect_error = Some(error.message.into()),
                    (Outcome::Ok(_), None) | (Outcome::Aborted, _) => {}
                }
                cx.notify();
            });
        }));
        cx.notify();
    }

    /// The bundle and Engine a deploy would use, read on this connection; `None` until there's an answer for both and the
    /// bundle is one this control deploys.
    pub(super) fn ready(&self, cx: &App) -> Option<(BundleFacts, String, String, String)> {
        let generation = self.current_generation(cx)?;
        let (facts, bundle, read_on) = self.facts.as_ref()?;
        let target = self.target.as_ref()?;
        let machine = self.machine.clone()?;
        (*read_on == generation
            && target.generation == generation
            && facts.refusal().is_none()
            && !self.occupied()
            && *bundle == self.path_text(cx))
        .then(|| {
            (
                facts.clone(),
                bundle.clone(),
                machine,
                target.engine.clone(),
            )
        })
    }

    /// An install is live, or its outcome hasn't been settled.
    pub(super) fn occupied(&self) -> bool {
        self.expected.is_some()
            && (self.unknown.is_some() || self.job.as_ref().is_none_or(|job| !job.settled()))
    }

    /// The owner's click: subscribes, then starts one install under a new id, which a retry would reuse.
    pub(super) fn deploy(&mut self, cx: &mut Context<Self>) {
        let Some((facts, bundle, machine, engine)) = self.ready(cx) else {
            return;
        };
        let operation_id = Uuid::new_v4().to_string();
        let args = json!({
            "operationId": operation_id,
            "machine": machine,
            "expectedEngine": engine,
            "bundleFile": bundle,
            "sha256": facts.sha256,
        });
        let expected = Expected {
            operation_id,
            machine,
            engine,
            commit: facts.commit,
        };
        cx.set_global(LastDeploy(Some(expected.clone())));
        self.expected = Some(expected);
        (self.job, self.unknown, self.start_error, self.cancel_error) = (None, None, None, None);
        self.watch(Some(args), cx);
    }

    /// Reads the outcome again after losing sight of it: the same id, never a new start.
    pub(super) fn check_again(&mut self, cx: &mut Context<Self>) {
        self.unknown = None;
        self.watch(None, cx);
    }

    /// The owner has looked at the machine and wants the form back. The backend still refuses a second job on the target.
    pub(super) fn forget(&mut self, cx: &mut Context<Self>) {
        cx.set_global(LastDeploy(None));
        self._watch = None;
        (
            self.expected,
            self.job,
            self.unknown,
            self.start_error,
            self.cancel_error,
        ) = (None, None, None, None, None);
        self.resolve_target(cx);
        cx.notify();
    }

    pub(super) fn cancel(&mut self, cx: &mut Context<Self>) {
        let (Some(expected), Some(socket)) = (self.expected.clone(), SocketClient::try_global(cx))
        else {
            return;
        };
        if !self.job.as_ref().is_some_and(Job::cancellable) {
            return;
        }
        let call = socket.read(cx).call::<Job>(
            FEATURE,
            "cancel",
            json!({"operationId": expected.operation_id}),
            None,
        );
        (self.cancelling, self.cancel_error) = (true, None);
        self._cancel = Some(cx.spawn(async move |form, cx| {
            let answer = call.await;
            _ = form.update(cx, |form, cx| {
                form.cancelling = false;
                match answer {
                    Outcome::Ok(job) => {
                        form.admit(job, cx);
                    }
                    Outcome::Failed(error) => form.cancel_error = Some(said(&error).into()),
                    Outcome::Aborted => {
                        form.cancel_error =
                            Some("The cancel may not have reached the backend.".into())
                    }
                }
                cx.notify();
            });
        }));
        cx.notify();
    }

    /// Takes a newer report of this install. True once it has settled.
    fn admit(&mut self, job: Job, cx: &mut Context<Self>) -> bool {
        let Some(expected) = &self.expected else {
            return false;
        };
        if Job::takes(self.job.as_ref(), &job, &expected.operation_id) {
            self.unknown = None;
            self.job = Some(job);
            cx.notify();
        }
        self.job.as_ref().is_some_and(Job::settled)
    }

    fn lost(&mut self, why: impl Into<SharedString>, cx: &mut Context<Self>) {
        self.unknown = Some(why.into());
        cx.notify();
    }

    /// Follows the install: subscribe, hear the first state, then start it or read its status, then push until it settles.
    fn watch(&mut self, start: Option<Value>, cx: &mut Context<Self>) {
        let (Some(expected), Some(socket)) = (self.expected.clone(), SocketClient::try_global(cx))
        else {
            self.lost("The backend isn't connected.", cx);
            return;
        };
        let id = expected.operation_id;
        let mut subscription = socket.read(cx).subscribe(FEATURE, Some(&id));
        self._watch = Some(cx.spawn(async move |form, cx| {
            let first = loop {
                match subscription.updates.recv().await {
                    Ok(Update::State(items)) => break items,
                    Ok(Update::Change { .. }) => {}
                    Err(_) => {
                        _ = form.update(cx, |form, cx| {
                            form.lost(
                                "This install can't be followed: its subscription was refused.",
                                cx,
                            )
                        });
                        return;
                    }
                }
            };
            let mut known = !first.is_empty();
            let mut done = Self::take(&form, first.into_iter().map(|(_, item)| item), cx);
            if let Some(args) = start {
                let call =
                    cx.update(|cx| socket.read(cx).call::<Job>(FEATURE, "start", args, None));
                match call.await {
                    Outcome::Ok(job) => {
                        known = true;
                        done |= form
                            .update(cx, |form, cx| form.admit(job, cx))
                            .unwrap_or(true);
                    }
                    Outcome::Failed(error) if refused_for_sure(&error) => {
                        _ = form.update(cx, |form, cx| {
                            cx.set_global(LastDeploy(None));
                            (form.expected, form.job) = (None, None);
                            form.start_error = Some(said(&error).into());
                            form.resolve_target(cx);
                            cx.notify();
                        });
                        return;
                    }
                    Outcome::Failed(_) | Outcome::Aborted => {}
                }
            }
            if !known {
                done |= Self::read_status(&form, &socket, &id, cx).await;
            }
            while !done {
                match subscription.updates.recv().await {
                    Ok(Update::State(items)) if items.is_empty() => {
                        done = Self::read_status(&form, &socket, &id, cx).await;
                    }
                    Ok(Update::State(items)) => {
                        done = Self::take(&form, items.into_iter().map(|(_, item)| item), cx);
                    }
                    Ok(Update::Change { item, .. }) => {
                        done = Self::take(&form, std::iter::once(item), cx);
                    }
                    Err(_) => {
                        _ = form.update(cx, |form, cx| {
                            form.lost("The outcome is unknown: the subscription ended.", cx)
                        });
                        return;
                    }
                }
            }
        }));
        cx.notify();
    }

    fn take(
        form: &WeakEntity<Self>,
        items: impl Iterator<Item = Value>,
        cx: &mut AsyncApp,
    ) -> bool {
        let mut done = false;
        for item in items.filter(|item| !item.is_null()) {
            done |= form
                .update(cx, |form, cx| match serde_json::from_value::<Job>(item) {
                    Ok(job) => form.admit(job, cx),
                    Err(_) => {
                        form.lost(
                            "The outcome is unknown: the backend's report couldn't be read.",
                            cx,
                        );
                        false
                    }
                })
                .unwrap_or(true);
        }
        done
    }

    /// One status read, on a reopen or a reconnect. If the backend no longer holds the job, as after a restart of this
    /// computer's backend, the outcome is unknown; it's never taken for running or success. True when there's no more to hear.
    async fn read_status(
        form: &WeakEntity<Self>,
        socket: &Entity<SocketClient>,
        id: &str,
        cx: &mut AsyncApp,
    ) -> bool {
        let generation = cx.update(|cx| socket.read(cx).connection_generation());
        let call = cx.update(|cx| {
            socket
                .read(cx)
                .call::<Job>(FEATURE, "status", json!({"operationId": id}), None)
        });
        let answer = call.await;
        form.update(cx, |form, cx| {
            if form.current_generation(cx) != generation {
                return false;
            }
            match answer {
                Outcome::Ok(job) => form.admit(job, cx),
                Outcome::Failed(error) => {
                    form.lost(
                        match error.code {
                            ErrorCode::Refused { code } if code == "operation_unknown" => {
                                "The outcome is unknown: the backend no longer has a record of this install. Check the machine."
                            }
                            _ => "The outcome is unknown until the backend answers again.",
                        },
                        cx,
                    );
                    false
                }
                Outcome::Aborted => {
                    form.lost("The outcome is unknown until the backend answers again.", cx);
                    false
                }
            }
        })
        .unwrap_or(true)
    }
}

/// What a refusal says: the words for a code on the backend's list, else its own message.
fn said(error: &CallFailure) -> String {
    match &error.code {
        ErrorCode::Refused { code } => refusal_words(code).to_string(),
        _ => error.message.clone(),
    }
}

/// Whether the backend said for certain that this start never began. Anything else may have reached it.
fn refused_for_sure(error: &CallFailure) -> bool {
    matches!(
        error.code,
        ErrorCode::BadArgs | ErrorCode::NotFound | ErrorCode::Refused { .. }
    )
}

#[path = "deploy_target.rs"]
mod deploy_target;

#[cfg(test)]
#[path = "deploy_form_tests.rs"]
mod tests;

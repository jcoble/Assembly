//! The inside of one machine line in a provider's grower: its Advanced section and the calls behind it.
//! Advanced holds two text fields — the CLI path and the config folder — read from that machine through
//! the backend's `providers/runtimeRead` call and saved back through `runtimeSave`, which writes them into
//! that machine's own agents file, the one descriptor every check, launch and CLI probe on it captures.
//! (No stock adapter or CLI takes a separate account folder or launch arguments from the descriptor, so
//! Advanced doesn't offer them; the values the read returns for them are sent back unchanged.)
//! "Try again" on a failure sends the engine's check command for that provider on that machine. The panel
//! decides none of the values; it shows what the call returns and sends what the person typed.

use gpui_kit::component::button::Button;
use gpui_kit::component::input::{Input, InputState};
use gpui_kit::component::{ActiveTheme as _, Disableable as _, Sizable as _, h_flex, v_flex};
use gpui_kit::*;
use serde::Deserialize;
use serde_json::json;

use super::home_panel::HomePanel;
use super::settings_state::{ProviderOnMachine, RuntimeSettings};
use crate::core::socket::client::SocketClient;
use crate::core::socket::error_code::ErrorCode;
use crate::core::socket::outcome::Outcome;
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::core::ui::access::Control as _;
use crate::core::ui::access::control_ids::home_provider_machine;
use crate::core::ui::access::say;

/// The two fields' labels, in the order the fields are held.
const LABELS: [&str; 2] = ["CLI path", "Config folder"];
/// The accessibility id part of each of the two fields, in the same order.
const PARTS: [&str; 2] = ["cli-path", "config-folder"];

/// One machine line's Advanced editor: its two fields once made, and where its read and save stand.
#[derive(Default)]
pub(super) struct MachineEdit {
    /// The text fields, made when the grower first draws with values to show.
    pub(super) fields: Option<[Entity<InputState>; 2]>,
    /// The values the fields were filled with (and a save last landed); a field still holding its
    /// filled value is one the person did not change.
    filled: Option<[Option<String>; 2]>,
    /// The values the last read or save returned; the fields are made from these.
    loaded: Option<RuntimeSettings>,
    /// The revision the last read or save returned, which a save must name.
    revision: String,
    /// A read is in flight.
    reading: bool,
    /// The last read failed, so the grower says so instead of showing fields.
    read_failed: bool,
    /// A save is in flight.
    saving: bool,
    /// What the last save or read has to say, under the fields.
    pub(super) notice: Option<String>,
}

/// The key of one machine line's editor in the panel's map.
pub(super) fn edit_key(provider: &str, machine_key: &str) -> String {
    format!("{provider}/{machine_key}")
}

/// What the engine answers a command with; only whether it applied matters here.
#[derive(Deserialize)]
#[allow(dead_code)]
struct CommandResult {
    applied: bool,
}

/// What `runtimeRead` and `runtimeSave` answer with: the machine's current Advanced values and revision.
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct RuntimeReply {
    revision: String,
    settings: RuntimeSettings,
}

impl HomePanel {
    /// The Advanced grower's body: the two fields and Save, or why they aren't there yet. The first draw
    /// starts the read; the fields are made when its answer is in.
    pub(super) fn advanced_body(
        &mut self,
        provider: &str,
        on: &ProviderOnMachine,
        offline: bool,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> AnyElement {
        let key = edit_key(provider, &on.machine_key);
        // The fields are made here, where a window is at hand, from the values the read brought.
        let loaded = self.edits.get(&key).and_then(|edit| edit.loaded.clone());
        let has_fields = self
            .edits
            .get(&key)
            .is_some_and(|edit| edit.fields.is_some());
        if !has_fields && let Some(settings) = loaded {
            let fields: [Entity<InputState>; 2] = std::array::from_fn(|index| {
                cx.new(|cx| InputState::new(window, cx).placeholder(LABELS[index]))
            });
            let values = [
                settings.binary_path.clone().unwrap_or_default(),
                settings.config_home.clone().unwrap_or_default(),
            ];
            for (field, value) in fields.iter().zip(values) {
                field.update(cx, |field, cx| field.set_value(value, window, cx));
            }
            let edit = self.edits.entry(key.clone()).or_default();
            edit.filled = Some([settings.binary_path.clone(), settings.config_home.clone()]);
            edit.fields = Some(fields);
        }
        let edit = self.edits.entry(key.clone()).or_default();
        if edit.fields.is_none() {
            if offline {
                return note("This machine is offline.", cx).into_any_element();
            }
            if edit.read_failed {
                return note("Couldn't load these settings.", cx).into_any_element();
            }
            // Nothing to show until the read answers; start it once.
            let needs_read = !edit.reading;
            let engine = on.engine_machine.clone();
            if needs_read && let Some(engine) = engine {
                self.start_read(provider, &engine, &key, cx);
            }
            return note("Loading…", cx).into_any_element();
        }
        let fields = edit.fields.clone().unwrap();
        let notice = edit.notice.clone();
        let saving = edit.saving;
        let (provider, machine) = (provider.to_string(), on.machine_key.clone());
        let (save_provider, save_machine) = (provider.clone(), machine.clone());
        let engine = on.engine_machine.clone();
        let mut body = v_flex().gap_1p5().pt_1();
        for (index, field) in fields.iter().enumerate() {
            body = body.child(field_row(
                LABELS[index],
                home_provider_machine(&provider, &machine, PARTS[index]),
                field,
                cx,
            ));
        }
        body = body.child(
            h_flex()
                .gap_2()
                .items_center()
                .child(
                    Button::new(format!("home-advanced-save-{provider}-{machine}"))
                        .debug_selector({
                            let selector = format!("home-advanced-save-{provider}-{machine}");
                            move || selector.clone()
                        })
                        .small()
                        .label("Save")
                        .control(
                            home_provider_machine(&provider, &machine, "advanced-save"),
                            "Save",
                        )
                        .disabled(saving)
                        .on_click(cx.listener(move |panel, _, _, cx| {
                            if let Some(engine) = &engine {
                                panel.save_advanced(&save_provider, engine, &save_machine, cx);
                            }
                        })),
                )
                .children(notice.map(|words| {
                    say(
                        home_provider_machine(&provider, &machine, "advanced-notice"),
                        words,
                    )
                    .text_role(TextRole::Meta, cx)
                    .text_color(cx.theme().muted_foreground)
                })),
        );
        body.into_any_element()
    }

    /// Reads the machine's current Advanced values through the backend call that machine answers.
    fn start_read(
        &mut self,
        provider: &str,
        engine_machine: &str,
        key: &str,
        cx: &mut Context<Self>,
    ) {
        let Some(client) = SocketClient::try_global(cx) else {
            return;
        };
        self.edits.get_mut(key).unwrap().reading = true;
        let call = client.read(cx).call::<RuntimeReply>(
            "providers",
            "runtimeRead",
            json!({"machine": engine_machine, "provider": provider}),
            None,
        );
        let key = key.to_string();
        self.calls.push(cx.spawn(async move |this, cx| {
            let outcome = call.await;
            _ = this.update(cx, |panel, cx| {
                let edit = panel.edits.entry(key).or_default();
                edit.reading = false;
                match outcome {
                    Outcome::Ok(reply) => {
                        edit.revision = reply.revision;
                        edit.loaded = Some(reply.settings);
                        edit.read_failed = false;
                    }
                    _ => edit.read_failed = true,
                }
                cx.notify();
            });
        }));
    }

    /// Sends the two fields' text as the machine's Advanced values. Empty text clears an override. The
    /// account folder and launch arguments the read returned go back exactly as they came: the backend
    /// refuses a change to them, and the array keeps its boundaries.
    fn save_advanced(
        &mut self,
        provider: &str,
        engine_machine: &str,
        machine_key: &str,
        cx: &mut Context<Self>,
    ) {
        let key = edit_key(provider, machine_key);
        let Some(edit) = self.edits.get_mut(&key) else {
            return;
        };
        let Some(fields) = edit.fields.clone() else {
            return;
        };
        if edit.saving {
            return;
        }
        let text: Vec<String> = fields
            .iter()
            .map(|field| field.read(cx).value().to_string())
            .collect();
        let value = |index: usize| {
            let trimmed = text[index].trim();
            (!trimmed.is_empty()).then(|| trimmed.to_string())
        };
        let loaded = edit.loaded.clone().unwrap_or_default();
        // Only what changed is this editor's to write: a field still holding the value it was
        // filled with sends the latest read's value, so a retry after a stale-revision refresh
        // cannot put back the text another editor has since replaced.
        let filled = edit.filled.clone();
        let current = [loaded.binary_path.clone(), loaded.config_home.clone()];
        let merged: [Option<String>; 2] = std::array::from_fn(|index| {
            let typed = value(index);
            match &filled {
                Some(filled) if filled[index] == typed => current[index].clone(),
                _ => typed,
            }
        });
        // What each field held when this save was sent: the reply rewrites only a field that
        // still holds exactly this, so typing during the save is never discarded.
        let sent: [Option<String>; 2] = std::array::from_fn(|index| value(index));
        let args = json!({
            "machine": engine_machine,
            "provider": provider,
            "expectedRevision": edit.revision,
            "settings": {
                "binaryPath": merged[0],
                "configHome": merged[1],
                "shadowHome": loaded.shadow_home,
                "launchArguments": loaded.launch_arguments,
            },
        });
        edit.saving = true;
        edit.notice = None;
        cx.notify();
        let Some(client) = SocketClient::try_global(cx) else {
            return;
        };
        let call = client
            .read(cx)
            .call::<RuntimeReply>("providers", "runtimeSave", args, None);
        let (provider, engine) = (provider.to_string(), engine_machine.to_string());
        self.calls.push(cx.spawn(async move |this, cx| {
            let outcome = call.await;
            let stale = this
                .update_in(cx, |panel, window, cx| {
                    let edit = panel.edits.entry(key.clone()).or_default();
                    edit.saving = false;
                    let stale = match outcome {
                        Outcome::Ok(reply) => {
                            edit.revision = reply.revision;
                            // What landed is the new "unchanged" mark for the next save, and
                            // the inputs show it too: text and mark must never disagree, or a
                            // later save would send the stale text as a fresh edit.
                            edit.filled = Some([
                                reply.settings.binary_path.clone(),
                                reply.settings.config_home.clone(),
                            ]);
                            if let Some(fields) = edit.fields.clone() {
                                let values = [
                                    reply.settings.binary_path.clone().unwrap_or_default(),
                                    reply.settings.config_home.clone().unwrap_or_default(),
                                ];
                                for (index, (field, value)) in
                                    fields.iter().zip(values).enumerate()
                                {
                                    // A field edited while the save was in flight keeps the
                                    // person's text; against the landed mark it reads as
                                    // changed, so the next save sends it as their edit.
                                    let now = field.read(cx).value().to_string();
                                    let now = now.trim();
                                    let now = (!now.is_empty()).then(|| now.to_string());
                                    if now == sent[index] {
                                        field.update(cx, |field, cx| {
                                            field.set_value(value, window, cx)
                                        });
                                    }
                                }
                            }
                            edit.loaded = Some(reply.settings);
                            edit.notice = Some("Saved.".into());
                            false
                        }
                        // Another editor saved first: this editor's revision is stale. The typed
                        // text stays; the refresh below brings the current values and revision.
                        Outcome::Failed(failure)
                            if matches!(&failure.code, ErrorCode::Refused { code } if code == "runtime_settings_conflict") =>
                        {
                            edit.notice = Some(
                                "The settings changed, so this edit wasn't saved. \
                                 Your text is still here — save again."
                                    .into(),
                            );
                            true
                        }
                        Outcome::Failed(failure) => {
                            edit.notice = Some(if failure.message.is_empty() {
                                "Couldn't save those settings.".into()
                            } else {
                                failure.message.clone()
                            });
                            false
                        }
                        Outcome::Aborted => false,
                    };
                    cx.notify();
                    stale
                })
                .unwrap_or(false);
            if stale {
                _ = this.update(cx, |panel, cx| {
                    panel.start_read(&provider, &engine, &key, cx)
                });
            }
        }));
    }

    /// Re-reads a machine line's values when its Advanced grower is reopened, so a revision another
    /// editor moved is picked up before the next save. The fields keep what was typed.
    pub(super) fn refresh_edit(
        &mut self,
        provider_id: &str,
        machine_key: &str,
        cx: &mut Context<Self>,
    ) {
        let key = edit_key(provider_id, machine_key);
        let Some(edit) = self.edits.get(&key) else {
            return;
        };
        if edit.reading || edit.fields.is_none() {
            return;
        }
        let engine = self.state.as_ref().and_then(|state| {
            state
                .providers
                .iter()
                .find(|provider| provider.id == provider_id)
                .and_then(|provider| {
                    provider
                        .machines
                        .iter()
                        .find(|on| on.machine_key == machine_key)
                })
                .and_then(|on| on.engine_machine.clone())
        });
        if let Some(engine) = engine {
            self.start_read(provider_id, &engine, &key, cx);
        }
    }

    /// "Try again" on a failure retries what the backend said failed: an install failure re-runs
    /// the install (this machine through its reconcile, a remote through the one update start),
    /// anything else sends the engine's check command for this provider on this machine, at the
    /// row version the backend last pushed. The answer arrives as the next pushed state.
    pub(super) fn try_again(&mut self, on: &ProviderOnMachine, cx: &mut Context<Self>) {
        if on
            .last_failure
            .as_ref()
            .and_then(|failure| failure.retry.as_deref())
            == Some("install")
        {
            if on.machine_key == "local" {
                self.check_again(cx);
                return;
            }
            let Some(client) = SocketClient::try_global(cx) else {
                return;
            };
            // Only this failure's machine: its own update start, never every machine's.
            let call = client.read(cx).call::<serde_json::Value>(
                "updates",
                "startMachine",
                json!({"machine": on.machine_key}),
                None,
            );
            self.calls.push(cx.spawn(async move |_, _| {
                _ = call.await;
            }));
            return;
        }
        let (Some(engine), Some(target)) = (&on.engine_machine, &on.provider_target_id) else {
            return;
        };
        let Some(client) = SocketClient::try_global(cx) else {
            return;
        };
        let args = json!({
            "commandId": uuid::Uuid::new_v4().to_string(),
            "entityId": target,
            "expectedVersion": on.version,
            "epoch": 0,
            "command": {"kind": "check_provider"},
            "machine": engine,
        });
        let call = client
            .read(cx)
            .call::<CommandResult>("engine", "command", args, None);
        self.calls.push(cx.spawn(async move |_, _| {
            // Nothing to show here: the pushed state says what the check found.
            _ = call.await;
        }));
    }
}

/// One labelled text field of the Advanced section.
fn field_row(label: &'static str, id: String, state: &Entity<InputState>, cx: &App) -> Div {
    v_flex()
        .gap_0p5()
        .child(
            div()
                .text_role(TextRole::Meta, cx)
                .text_color(cx.theme().muted_foreground)
                .child(label),
        )
        .child(Input::new(state).control(id, label).small())
}

/// A muted line of text, used while the Advanced values load and when they can't.
fn note(words: &'static str, cx: &App) -> Div {
    div()
        .py_1()
        .text_role(TextRole::Meta, cx)
        .text_color(cx.theme().muted_foreground)
        .child(words)
}

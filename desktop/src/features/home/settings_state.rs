//! What the backend pushes on the `settings_state` topic for the Home panel: one item, key `current`, holding the
//! summary of what needs the user, each provider with one state per machine, and each machine. Every state here is
//! derived by the backend; the panel only turns each into words and a colour. Reading is tolerant: an unknown field is
//! ignored and a missing one takes its default, so an older or newer backend still shows what it sends.

use serde::Deserialize;

/// The whole pushed answer.
#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default, rename_all = "camelCase")]
pub struct SettingsState {
    pub summary: Summary,
    pub providers: Vec<Provider>,
    pub machines: Vec<Machine>,
}

/// The panel's top line: what kind of thing needs the user, and the backend's plain sentence for it.
#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default, rename_all = "camelCase")]
pub struct Summary {
    /// `nothing`, `problem`, `needs_sign_in`, `waiting_on_sessions` or `restart_to_finish`.
    pub kind: String,
    /// Null when nothing needs the user.
    pub message: Option<String>,
}

/// One provider across every machine.
#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default, rename_all = "camelCase")]
pub struct Provider {
    /// The engine's provider id: `claude`, `codex`.
    pub id: String,
    /// The display name; absent from an older backend, then the panel names it from `id`.
    pub name: Option<String>,
    /// A bundled icon's path, picked by the backend; absent, the row has no icon.
    pub glyph: Option<String>,
    pub enabled: bool,
    /// `ready`, `setting_up`, `needs_sign_in` or `problem`; null while switched off.
    pub state: Option<String>,
    pub machines: Vec<ProviderOnMachine>,
}

/// One provider on one machine, as the provider's grower shows it.
#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default, rename_all = "camelCase")]
pub struct ProviderOnMachine {
    /// The machine's key in `machines`: `local` or the remote's name.
    pub machine_key: String,
    /// The same four states as the provider's row.
    pub state: Option<String>,
    pub adapter_version: Option<String>,
    pub cli_version: Option<String>,
    pub models: Vec<Model>,
    pub last_failure: Option<Failure>,
}

#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default)]
pub struct Model {
    pub id: String,
    pub name: Option<String>,
}

/// Why the last check or install failed: one plain sentence, and the raw detail for a Details grower.
#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default)]
pub struct Failure {
    pub message: String,
    pub detail: Option<String>,
}

/// One machine: this one or a remote.
#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default, rename_all = "camelCase")]
pub struct Machine {
    /// `local` or the remote's name; `ProviderOnMachine::machine_key` points here.
    pub key: String,
    pub display_name: Option<String>,
    /// `connected`, `checking`, `offline` or `disconnected`.
    pub connection_state: Option<String>,
    /// `up_to_date`, `unknown`, `update_available`, `updating`, `waiting_on_sessions`, `restart_required`,
    /// `restarting`, `failed` or `cant_update`.
    pub update_state: Option<String>,
}

impl Machine {
    /// What the panel calls it: the backend's display name, else its key.
    pub fn label(&self) -> &str {
        self.display_name.as_deref().unwrap_or(&self.key)
    }
}

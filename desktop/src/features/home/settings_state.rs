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

/// The panel's top line: what kind of thing needs the user, and the backend's plain sentence for it (also when
/// nothing does), plus backend-derived flags for the Home button's progress ring (`working`) and amber state
/// (`needs_user`).
#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default, rename_all = "camelCase")]
pub struct Summary {
    /// `nothing`, `problem`, `needs_sign_in`, `waiting_on_sessions` or `restart_to_finish`.
    pub kind: String,
    pub message: String,
    /// True while Assembly is checking, installing or updating on its own; drives the Home button's progress ring.
    pub working: bool,
    /// True only when something needs the user; turns the Home button amber.
    pub needs_user: bool,
    /// The staged update's version when a restart would finish it, whatever the top line is about;
    /// the panel shows Restart beside any summary while this is set.
    pub restart_version: Option<String>,
}

/// One provider across every machine.
#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default, rename_all = "camelCase")]
pub struct Provider {
    /// The engine's provider id: `claude`, `codex`.
    pub id: String,
    pub name: String,
    /// A bundled icon's path, picked by the backend.
    pub glyph: String,
    pub enabled: bool,
    /// `ready`, `setting_up`, `needs_sign_in` or `problem`; null while switched off.
    pub state: Option<String>,
    /// The model list when every machine that knows its list agrees; null when they differ, and then each machine
    /// shows its own.
    pub models: Option<Vec<Model>>,
    pub machines: Vec<ProviderOnMachine>,
}

/// One provider on one machine, as the provider's grower shows it.
#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default, rename_all = "camelCase")]
pub struct ProviderOnMachine {
    /// The machine's key in `machines`: `local` or the remote's name.
    pub machine_key: String,
    /// That machine's own engine id, which its calls are addressed to; null until it is known.
    pub engine_machine: Option<String>,
    /// The same four states as the provider's row.
    pub state: Option<String>,
    pub adapter_version: Option<String>,
    pub cli_version: Option<String>,
    pub models: Option<Vec<Model>>,
    pub last_failure: Option<Failure>,
    /// `provider/account`, the engine row the check command addresses.
    pub provider_target_id: Option<String>,
    /// The engine row's version, which a check command must name to be applied.
    pub version: i64,
}

/// A provider's runtime values on one machine, as the backend's runtime call reads and saves them. Advanced
/// edits the CLI path and the config folder; the account folder and launch arguments are legacy values the
/// read returns and a save sends back unchanged. Empty text means no override.
#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default, rename_all = "camelCase")]
pub struct RuntimeSettings {
    pub binary_path: Option<String>,
    pub config_home: Option<String>,
    pub shadow_home: Option<String>,
    pub launch_arguments: Vec<String>,
}

#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default)]
pub struct Model {
    pub id: String,
    pub name: String,
}

/// Why the last check or install failed: one plain sentence, and the raw detail for a Details grower.
#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default)]
pub struct Failure {
    pub message: String,
    pub detail: String,
}

/// One machine: this one or a remote. The backend also sends the full machine item under `machine`; the panel
/// doesn't read it yet.
#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default, rename_all = "camelCase")]
pub struct Machine {
    /// `local` or the remote's name; `ProviderOnMachine::machine_key` points here.
    pub key: String,
    pub display_name: String,
    /// `connected`, `checking`, `offline` or `disconnected`.
    pub connection_state: Option<String>,
    /// `up_to_date`, `unknown`, `update_available`, `updating`, `waiting_on_sessions`, `restart_required`,
    /// `restarting`, `failed` or `cant_update`.
    pub update_state: Option<String>,
}

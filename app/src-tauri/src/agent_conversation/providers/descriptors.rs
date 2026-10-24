//! Providers are data: each one Assembly can run is a descriptor in `descriptors.json`, and code
//! asks the provider's descriptor instead of matching on its name (workflow redesign plan 0.2).
//! Adding an ACP agent is a new descriptor plus its release-bundle entry.
//!
//! Evidence for today's values:
//! - `initialize_meta`: Codex reads `recommendedValue` for its default effort, and codex-acp 2.2.2 sends
//!   command output only to a client that takes `terminal_output_delta` (TSK-1589 frame logs); Claude ACP 0.88/0.93
//!   read `asyncTasks`, `nativeSubagentSessions` and `backgroundSubagents` (l9/y3, runtime bytes
//!   82788482/92449895). Claude answers forms through `elicitation`.
//! - `effort_in_model_id`: Codex ACP 1.13.1/2.2.2 emit `model[effort]` (codex-1.13.1-isolated.jsonl:6).
//! - `canonical_cwd`: Antigravity 1.2.1 server.py:3264-3268 binds restored tool permissions to
//!   realpath(cwd).
//! - `unattended`: the auto-review modes, Claude 0.88/0.93 `auto` and Codex 1.13.1/2.2.2 `agent`;
//!   Antigravity has none (its modes are default, auto_edit and yolo).
//! - `pre_approval` lets unattended workers use Assembly's own tools without asking: Claude's
//!   `allowedTools` meta, Codex's `default_tools_approval_mode` config, and for Antigravity, which
//!   has neither, Assembly's permission handler answers them (TSK-1522; its MCP requests carry
//!   `_meta.mcp.server`, Antigravity 1.2.1 tools.py:386-391, server.py:4400). Antigravity lets a server Assembly supplies at session start replace a configured one
//!   of the same name (mcp_servers.py:130-147), and every Assembly session supplies its `assembly`
//!   endpoint, so that name is Assembly's endpoint. Only Allow (once) is chosen, never Allow Always.
//! - `login`: Claude reads an empty refresh token as expired, so it is deleted; Codex can't read
//!   `auth.json` without the field (token_data.rs:22), so it is blanked. Every refresh token in the
//!   file goes (Claude's `mcpOAuth` entries hold their own); access tokens stay for the proof
//!   session. Codex's `OPENAI_API_KEY` stays: it is an API key, not a way to renew the login. Antigravity's saved login
//!   holds only a refresh token, so it has no proof home and can't prove (plan 3.11).

use std::sync::LazyLock;

use serde::{Deserialize, Deserializer, Serialize, Serializer};
use serde_json::{Map, Value};

use crate::agent_conversation::protocol::AgentConversationProvider;

#[derive(Clone, Debug, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ProviderDescriptor {
    pub id: String,
    /// The adapter manifest id it launches (`AgentProviderManifest::id`).
    pub adapter_id: String,
    pub display_name: String,
    #[serde(default)]
    pub initialize_meta: Option<Map<String, Value>>,
    #[serde(default)]
    pub elicitation: bool,
    #[serde(default)]
    pub effort_in_model_id: bool,
    #[serde(default)]
    pub canonical_cwd: bool,
    #[serde(default)]
    pub unattended: LaunchSetting,
    #[serde(default)]
    pub pre_approval: LaunchSetting,
    /// What moves its config folder into a proof run's provider home (`{home}`); missing means
    /// the provider is unavailable in proof mode.
    #[serde(default)]
    pub proof_home: Option<LaunchSetting>,
    /// Missing means it can't run read-only, so it can't prove.
    #[serde(default)]
    pub read_only: Option<LaunchSetting>,
    /// Read by proof mode (plan 3.11), a later piece.
    #[allow(dead_code)]
    #[serde(default)]
    pub login: Option<LoginEntry>,
}

/// One launch setting: a session mode, `session/new` `_meta`, adapter arguments and environment.
/// A JSON object in `env` is merged into the variable's inherited JSON value.
#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default, deny_unknown_fields)]
pub struct LaunchSetting {
    pub mode: Option<String>,
    pub meta: Map<String, Value>,
    pub args: Vec<String>,
    pub env: Map<String, Value>,
    /// Assembly's permission handler approves the agent's requests for `assembly` MCP tools.
    pub permission_handler: bool,
}

/// Which launch settings a session starts with; they merge in this order, `read_only` last.
#[derive(Clone, Copy, Debug, Default)]
pub struct LaunchFor {
    pub unattended: bool,
    pub pre_approval: bool,
    pub proof: bool,
    pub read_only: bool,
}

/// How a copied login loses its refresh token, so a proof session can't renew the real login.
#[derive(Clone, Debug, Deserialize, PartialEq)]
#[serde(deny_unknown_fields)]
pub struct LoginEntry {
    /// The login file, relative to the provider's config folder.
    pub file: String,
    /// JSON pointers into that file.
    pub refresh_field: String,
    pub action: LoginAction,
}

#[derive(Clone, Copy, Debug, Deserialize, PartialEq)]
#[serde(rename_all = "lowercase")]
pub enum LoginAction {
    Delete,
    Blank,
}

static DESCRIPTORS: LazyLock<Vec<ProviderDescriptor>> = LazyLock::new(|| {
    parse_manifest(include_str!("descriptors.json")).expect("descriptors.json is a valid manifest")
});

pub fn parse_manifest(text: &str) -> Result<Vec<ProviderDescriptor>, String> {
    #[derive(Deserialize)]
    #[serde(deny_unknown_fields)]
    struct Manifest {
        providers: Vec<ProviderDescriptor>,
    }
    let providers = serde_json::from_str::<Manifest>(text).map_err(|error| format!("Invalid provider manifest: {error}"))?.providers;
    for (index, descriptor) in providers.iter().enumerate() {
        if providers[..index].iter().any(|earlier| earlier.id == descriptor.id || earlier.adapter_id == descriptor.adapter_id) {
            return Err(format!("Provider manifest lists {} twice", descriptor.id));
        }
    }
    Ok(providers)
}

pub fn all() -> &'static [ProviderDescriptor] {
    &DESCRIPTORS
}

/// The descriptor that launches this adapter manifest.
pub fn for_adapter(adapter_id: &str) -> Option<&'static ProviderDescriptor> {
    all().iter().find(|descriptor| descriptor.adapter_id == adapter_id)
}

/// Assembly's own answer to an ACP permission request, when the provider's pre-approval asks the
/// permission handler to allow Assembly's MCP tools: the request's allow-once option.
pub fn handler_answer(provider: AgentConversationProvider, params: &Value) -> Option<Value> {
    let assembly_tool = params.pointer("/toolCall/_meta/is_mcp_tool_call") == Some(&Value::Bool(true))
        && params.pointer("/toolCall/_meta/mcp/server").and_then(Value::as_str) == Some("assembly");
    if !provider.descriptor().pre_approval.permission_handler || !assembly_tool {
        return None;
    }
    let option = params.get("options")?.as_array()?.iter()
        .find(|option| option.get("kind").and_then(Value::as_str) == Some("allow_once"))?;
    let option_id = option.get("optionId").or_else(|| option.get("option_id"))?;
    Some(serde_json::json!({"outcome": {"outcome": "selected", "optionId": option_id}}))
}

/// The value to set for `key`, given what it would inherit.
pub fn env_value(key: &str, inherited: Option<&str>, value: &Value) -> Result<String, String> {
    match value {
        Value::String(value) => Ok(value.clone()),
        Value::Object(value) => {
            let mut merged = match inherited {
                Some(inherited) => serde_json::from_str(inherited).map_err(|error| format!("Invalid adapter {key}: {error}"))?,
                None => Map::new(),
            };
            deep_merge(&mut merged, value);
            Ok(Value::Object(merged).to_string())
        }
        value => Ok(value.to_string()),
    }
}

/// Objects merge key by key; anything else from `from` replaces what is there.
fn deep_merge(into: &mut Map<String, Value>, from: &Map<String, Value>) {
    for (key, value) in from {
        match (into.get_mut(key), value) {
            (Some(Value::Object(current)), Value::Object(value)) => deep_merge(current, value),
            _ => {
                into.insert(key.clone(), value.clone());
            }
        }
    }
}

impl ProviderDescriptor {
    pub fn provider(&'static self) -> AgentConversationProvider {
        AgentConversationProvider(self.id.as_str())
    }

    /// The merged launch settings, or `None` when a wanted one is missing.
    pub fn launch(&self, wanted: LaunchFor) -> Option<LaunchSetting> {
        let mut merged = LaunchSetting::default();
        for (on, setting) in [
            (wanted.unattended, Some(&self.unattended)),
            (wanted.pre_approval, Some(&self.pre_approval)),
            (wanted.proof, self.proof_home.as_ref()),
            (wanted.read_only, self.read_only.as_ref()),
        ] {
            if on {
                let setting = setting?;
                merged.mode = setting.mode.clone().or(merged.mode);
                deep_merge(&mut merged.meta, &setting.meta);
                merged.args.extend(setting.args.iter().cloned());
                deep_merge(&mut merged.env, &setting.env);
                merged.permission_handler |= setting.permission_handler;
            }
        }
        Some(merged)
    }
}

/// One provider as Settings → Plugins and Providers shows it.
#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ProviderSummary {
    pub id: String,
    pub display_name: String,
    pub adapter_id: String,
    /// The installed adapter's version; `None` when it isn't installed.
    pub adapter_version: Option<String>,
    pub read_only: bool,
    pub proof_home: bool,
    pub permission_handler: bool,
}

impl ProviderDescriptor {
    pub fn summary(&self, adapter_version: Option<String>) -> ProviderSummary {
        ProviderSummary {
            id: self.id.clone(),
            display_name: self.display_name.clone(),
            adapter_id: self.adapter_id.clone(),
            adapter_version,
            read_only: self.read_only.is_some(),
            proof_home: self.proof_home.is_some(),
            permission_handler: self.pre_approval.permission_handler,
        }
    }
}

#[tauri::command]
pub fn list_provider_descriptors(
    manager: tauri::State<'_, crate::agent_conversation::manager::AgentRuntimeManager>,
) -> Vec<ProviderSummary> {
    all().iter()
        .map(|descriptor| descriptor.summary(manager.providers().manifest(descriptor.provider()).ok().map(|manifest| manifest.version)))
        .collect()
}

#[allow(dead_code)] // Used by proof mode (plan 3.11), a later piece.
impl LoginEntry {
    /// A copy of the login with every refresh token removed; an error when the descriptor's
    /// refresh field isn't there, because the file's format has changed.
    pub fn stripped(&self, login: &Value) -> Result<Value, String> {
        if login.pointer(&self.refresh_field).is_none() {
            return Err(format!("The login in {} has no {}", self.file, self.refresh_field));
        }
        let mut copy = login.clone();
        Self::strip_refresh_tokens(&mut copy, self.action);
        Ok(copy)
    }

    fn strip_refresh_tokens(value: &mut Value, action: LoginAction) {
        let is_refresh_token = |key: &str| key.to_ascii_lowercase().replace('_', "") == "refreshtoken";
        match value {
            Value::Object(object) => {
                if action == LoginAction::Delete {
                    object.retain(|key, _| !is_refresh_token(key));
                }
                for (key, value) in object.iter_mut() {
                    if is_refresh_token(key) {
                        *value = Value::String(String::new());
                    } else {
                        Self::strip_refresh_tokens(value, action);
                    }
                }
            }
            Value::Array(items) => items.iter_mut().for_each(|item| Self::strip_refresh_tokens(item, action)),
            _ => {}
        }
    }
}

#[allow(non_upper_case_globals)]
impl AgentConversationProvider {
    pub const Codex: Self = Self("codex");
    pub const Claude: Self = Self("claude");
    pub const Antigravity: Self = Self("antigravity");

    pub fn parse(value: &str) -> Option<Self> {
        let value = value.trim().to_ascii_lowercase();
        all().iter().find(|descriptor| descriptor.id == value).map(ProviderDescriptor::provider)
    }

    pub fn id(self) -> &'static str {
        self.0
    }

    pub fn descriptor(self) -> &'static ProviderDescriptor {
        all().iter().find(|descriptor| descriptor.id == self.0).expect("provider ids come from the manifest")
    }
}

impl Serialize for AgentConversationProvider {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        serializer.serialize_str(self.0)
    }
}

impl<'de> Deserialize<'de> for AgentConversationProvider {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        let value = String::deserialize(deserializer)?;
        let descriptor = all().iter().find(|descriptor| descriptor.id == value);
        descriptor.map(ProviderDescriptor::provider).ok_or_else(|| serde::de::Error::custom(format!("unknown provider {value}")))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn launch(id: &str, wanted: LaunchFor) -> Option<LaunchSetting> {
        AgentConversationProvider::parse(id).unwrap().descriptor().launch(wanted)
    }

    #[test]
    fn the_manifest_covers_todays_providers() {
        let ids: Vec<_> = all().iter().map(|descriptor| descriptor.id.as_str()).collect();
        assert_eq!(ids, ["codex", "claude", "antigravity"]);
        for provider in [AgentConversationProvider::Codex, AgentConversationProvider::Claude, AgentConversationProvider::Antigravity] {
            assert_eq!(AgentConversationProvider::parse(provider.id()), Some(provider));
            assert_eq!(serde_json::to_value(provider).unwrap(), json!(provider.id()));
            assert_eq!(serde_json::from_value::<AgentConversationProvider>(json!(provider.id())).unwrap(), provider);
        }
        assert!(serde_json::from_value::<AgentConversationProvider>(json!("grok")).is_err());
        // The wire stays exact; only transcript and workflow strings are trimmed and lower-cased.
        assert!(serde_json::from_value::<AgentConversationProvider>(json!("Claude")).is_err());
        assert!(serde_json::from_value::<AgentConversationProvider>(json!(" codex ")).is_err());
        assert_eq!(AgentConversationProvider::parse(" Codex "), Some(AgentConversationProvider::Codex));
        assert_eq!(for_adapter("codex-acp").unwrap().id, "codex");
        assert!(parse_manifest(r#"{"providers":[{"id":"a","adapter_id":"x","display_name":"A"},{"id":"a","adapter_id":"y","display_name":"B"}]}"#).is_err());
    }

    #[test]
    fn launch_settings_merge_in_order_with_read_only_last() {
        let all_on = LaunchFor { unattended: true, pre_approval: true, proof: true, read_only: true };
        let claude = launch("claude", all_on).unwrap();
        // Claude keeps auto under read_only: `tools: []` leaves it no built-in tools to run.
        assert_eq!(claude.mode.as_deref(), Some("auto"));
        assert_eq!(Value::Object(claude.meta), json!({"claudeCode": {"options": {"allowedTools": ["mcp__assembly"], "tools": []}}}));
        assert_eq!(claude.env, Map::from_iter([("CLAUDE_CONFIG_DIR".to_string(), json!("{home}"))]));
        let codex = launch("codex", all_on).unwrap();
        assert_eq!(codex.mode.as_deref(), Some("read-only"));
        assert_eq!(codex.env["CODEX_CONFIG"], json!({"mcp_servers.assembly.default_tools_approval_mode": "approve", "sandbox_mode": "read-only"}));
        let pre_approval = LaunchFor { pre_approval: true, ..Default::default() };
        assert!(launch("antigravity", pre_approval).unwrap().permission_handler);
        assert_eq!(launch("antigravity", LaunchFor { unattended: true, ..Default::default() }).unwrap().mode, None);
        assert!(launch("antigravity", LaunchFor { proof: true, ..Default::default() }).is_none());
        assert!(launch("antigravity", LaunchFor { read_only: true, ..Default::default() }).is_none());
    }

    #[test]
    fn a_descriptor_added_only_in_the_manifest_launches() {
        let manifest = r#"{"providers":[{"id":"grok","adapter_id":"grok-acp","display_name":"Grok Build",
            "unattended":{"mode":"auto","args":["--unattended"],"env":{"GROK_CONFIG":{"approval":"auto"}}},
            "pre_approval":{"args":["--allow-mcp","assembly"]},
            "read_only":{"mode":"plan","args":["--read-only"],"env":{"GROK_CONFIG":{"approval":"never","sandbox":"read-only"}}}}]}"#;
        let grok = &parse_manifest(manifest).unwrap()[0];
        let merged = grok.launch(LaunchFor { unattended: true, pre_approval: true, read_only: true, ..Default::default() }).unwrap();
        assert_eq!(merged.mode.as_deref(), Some("plan"));
        assert_eq!(merged.args, ["--unattended", "--allow-mcp", "assembly", "--read-only"]);
        let config = env_value("GROK_CONFIG", Some(r#"{"theme":"dark"}"#), &merged.env["GROK_CONFIG"]).unwrap();
        assert_eq!(serde_json::from_str::<Value>(&config).unwrap(), json!({"approval": "never", "sandbox": "read-only", "theme": "dark"}));
        assert!(env_value("GROK_CONFIG", Some("not json"), &merged.env["GROK_CONFIG"]).is_err());
        assert!(grok.launch(LaunchFor { proof: true, ..Default::default() }).is_none());
    }

    #[test]
    fn logins_are_copied_without_any_refresh_token() {
        let claude = AgentConversationProvider::Claude.descriptor().login.as_ref().unwrap();
        let login = json!({"claudeAiOauth": {"accessToken": "a", "refreshToken": "r", "expiresAt": 1},
            "mcpOAuth": {"server|1": {"accessToken": "m", "refreshToken": "mr"}}});
        assert_eq!(claude.stripped(&login).unwrap(), json!({"claudeAiOauth": {"accessToken": "a", "expiresAt": 1},
            "mcpOAuth": {"server|1": {"accessToken": "m"}}}));
        let codex = AgentConversationProvider::Codex.descriptor().login.as_ref().unwrap();
        let login = json!({"OPENAI_API_KEY": null, "tokens": {"access_token": "a", "refresh_token": "r"}, "last_refresh": "t"});
        assert_eq!(codex.stripped(&login).unwrap(),
            json!({"OPENAI_API_KEY": null, "tokens": {"access_token": "a", "refresh_token": ""}, "last_refresh": "t"}));
        // A login whose format moved the refresh token is refused, never copied with it.
        assert!(claude.stripped(&json!({"claudeAiOauth": {"accessToken": "a", "refresh": "r"}})).is_err());
        assert!(codex.stripped(&json!({"refresh_token": "r"})).is_err());
        let antigravity = AgentConversationProvider::Antigravity.descriptor();
        assert!(antigravity.login.is_none() && antigravity.proof_home.is_none());
    }

    #[test]
    fn settings_get_each_descriptor_with_its_installed_adapter() {
        let antigravity = AgentConversationProvider::Antigravity.descriptor().summary(Some("1.2.1".into()));
        assert_eq!(serde_json::to_value(&antigravity).unwrap(), json!({"id": "antigravity", "displayName": "Antigravity",
            "adapterId": "agy-acp", "adapterVersion": "1.2.1", "readOnly": false, "proofHome": false, "permissionHandler": true}));
        let codex = AgentConversationProvider::Codex.descriptor().summary(None);
        assert_eq!((codex.adapter_version, codex.read_only, codex.proof_home, codex.permission_handler), (None, true, true, false));
        let grok = &parse_manifest(r#"{"providers":[{"id":"grok","adapter_id":"grok-acp","display_name":"Grok Build","read_only":{"mode":"plan"}}]}"#).unwrap()[0];
        let grok = grok.summary(Some("0.1.0".into()));
        assert_eq!((grok.id.as_str(), grok.read_only, grok.proof_home), ("grok", true, false));
    }

    #[test]
    fn the_permission_handler_allows_assembly_tools_once_for_providers_that_ask_it() {
        // Antigravity 1.2.1 sends the MCP server in the tool call's _meta (tools.py:386-391), and lists
        // Allow Always before Allow (server.py:4380-4388).
        let options = json!([{"optionId": "allow_always", "kind": "allow_always"}, {"optionId": "allow", "kind": "allow_once"},
            {"optionId": "deny", "kind": "reject_once"}]);
        let request = |server: &str| json!({"options": options, "toolCall": {"_meta": {"mcp": {"tool": "report_result", "server": server}, "is_mcp_tool_call": true}}});
        let antigravity = AgentConversationProvider::Antigravity;
        assert_eq!(handler_answer(antigravity, &request("assembly")), Some(json!({"outcome": {"outcome": "selected", "optionId": "allow"}})));
        assert_eq!(handler_answer(antigravity, &request("notion")), None);
        assert_eq!(handler_answer(antigravity, &json!({"options": options, "toolCall": {"title": "assembly_report_result"}})), None);
        // With no allow-once choice the request goes to the owner rather than granting it for good.
        let mut always_only = request("assembly");
        always_only["options"] = json!([{"optionId": "allow_always", "kind": "allow_always"}]);
        assert_eq!(handler_answer(antigravity, &always_only), None);
        // Claude and Codex pre-approve in the adapter, so their requests still reach the owner.
        assert_eq!(handler_answer(AgentConversationProvider::Claude, &request("assembly")), None);
        assert_eq!(handler_answer(AgentConversationProvider::Codex, &request("assembly")), None);
    }
}

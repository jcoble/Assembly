//! `AgentConfig` is what `config/get` and `config/set` answer: the agent's own options (ACP config options: model,
//! effort, mode…), the values chosen for this session, and its slash commands. `options` is empty until the agent
//! has opened a session (ConfigFeature.cs).

use std::collections::HashMap;

use serde::Deserialize;

use super::slash_menu::SlashCommand;

#[derive(Clone, Debug, PartialEq, Deserialize)]
pub struct AgentConfig {
    #[serde(default)]
    pub options: Option<Vec<ConfigOption>>,
    pub choices: HashMap<String, String>,
    #[serde(default)]
    pub commands: Option<Vec<SlashCommand>>,
}

/// One of the agent's menus; only `select` ones show, each value its `options`' choice.
#[derive(Clone, Debug, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ConfigOption {
    pub id: String,
    pub name: String,
    pub category: Option<String>,
    #[serde(rename = "type")]
    pub kind: String,
    pub current_value: Option<String>,
    #[serde(default)]
    pub options: Vec<ConfigChoice>,
}

#[derive(Clone, Debug, PartialEq, Deserialize)]
pub struct ConfigChoice {
    pub value: String,
    pub name: String,
    pub description: Option<String>,
}

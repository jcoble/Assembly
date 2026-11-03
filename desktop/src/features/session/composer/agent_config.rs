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

impl AgentConfig {
    pub fn menus(&self) -> Vec<(&ConfigOption, &str)> {
        self.options
            .iter()
            .flatten()
            .filter(|option| option.kind == "select" && !option.options.is_empty())
            .map(|option| {
                (
                    option,
                    option
                        .current_value
                        .as_deref()
                        .filter(|value| option.offered(value))
                        .or_else(|| {
                            self.choices
                                .get(&option.id)
                                .map(String::as_str)
                                .filter(|value| option.offered(value))
                        })
                        .unwrap_or(""),
                )
            })
            .collect()
    }
}
impl ConfigOption {
    pub fn model(&self) -> bool {
        self.category.as_deref() == Some("model") || self.id == "model"
    }
    pub fn effort(&self) -> bool {
        matches!(self.category.as_deref(), Some("thought_level" | "effort"))
            || matches!(self.id.as_str(), "effort" | "reasoning_effort")
    }
    /// How much the agent may do without asking: its mode, or an approval policy by name.
    pub fn approval(&self) -> bool {
        self.category.as_deref() == Some("mode")
            || matches!(self.id.as_str(), "mode" | "approval_policy" | "approvalPolicy")
    }
    pub fn offered(&self, value: &str) -> bool {
        self.options.iter().any(|choice| choice.value == value)
    }
}

/// Safe configured-target metadata advertised by the actual owning machine.
#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentTarget {
    pub id: String,
    pub name: String,
    pub target_id: Option<String>,
    pub account_context: Option<String>,
    pub provider_target_id: Option<String>,
}
#[derive(Deserialize)]
pub struct AgentList {
    pub agents: Vec<AgentTarget>,
}

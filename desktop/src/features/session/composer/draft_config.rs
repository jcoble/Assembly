//! Provider-reported draft choices. Discovery is disposable; only chosen raw values enter create.
use super::agent_config::{AgentConfig, ConfigOption};
use crate::core::state::preferences::Preferences;
use serde::Deserialize;
use serde_json::{Value, json};
use std::collections::HashMap;

pub const PREFERENCE_KEY: &str = "composer.providerChoices";
#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Catalogue {
    #[serde(flatten)]
    pub config: AgentConfig,
    pub model_options: HashMap<String, Vec<ConfigOption>>,
}
pub struct DraftConfig {
    pub config: AgentConfig,
    catalogue: Catalogue,
}
impl DraftConfig {
    pub fn new(catalogue: Catalogue, remembered: Option<&Value>) -> Result<Self, String> {
        let model = catalogue
            .config
            .options
            .iter()
            .flatten()
            .find(|option| option.model() && option.kind == "select" && !option.options.is_empty())
            .ok_or("The provider did not advertise a model control.")?;
        let observed =
            |value: &&str| model.offered(value) && catalogue.model_options.contains_key(*value);
        let value = remembered
            .and_then(|v| v["model"].as_str())
            .filter(observed)
            .or_else(|| model.current_value.as_deref().filter(observed))
            .map(str::to_string);
        let id = model.id.clone();
        let mut draft = Self {
            config: catalogue.config.clone(),
            catalogue,
        };
        draft.config.choices.clear();
        let effort = remembered.and_then(|v| v["effort"].as_str());
        if let Some(value) = value {
            draft.select_model(&id, &value, effort)?;
        } else if let Some(model) = draft
            .config
            .options
            .as_mut()
            .and_then(|options| options.iter_mut().find(|o| o.id == id))
        {
            model.current_value = None;
        }
        Ok(draft)
    }
    pub fn ready(&self) -> bool {
        self.config
            .options
            .iter()
            .flatten()
            .filter(|o| o.model())
            .any(|o| {
                self.config.choices.get(&o.id).is_some_and(|value| {
                    o.offered(value) && self.catalogue.model_options.contains_key(value)
                })
            })
    }
    pub fn apply_remembered(&mut self, remembered: Option<&Value>) -> Result<(), String> {
        *self = Self::new(self.catalogue.clone(), remembered)?;
        Ok(())
    }
    pub fn choose(&mut self, id: &str, value: &str, effort: Option<&str>) -> Result<(), String> {
        let option = self
            .config
            .options
            .iter_mut()
            .flatten()
            .find(|option| option.id == id && option.kind == "select")
            .ok_or("This provider setting is unavailable.")?;
        if !option.offered(value) {
            return Err("The provider does not offer this value.".into());
        }
        if option.model() {
            return self.select_model(id, value, effort);
        }
        option.current_value = Some(value.into());
        self.config.choices.insert(id.into(), value.into());
        Ok(())
    }
    fn select_model(&mut self, id: &str, value: &str, effort: Option<&str>) -> Result<(), String> {
        let options = self
            .catalogue
            .model_options
            .get(value)
            .ok_or("Settings for this model are unavailable.")?;
        let mut choices = HashMap::new();
        for option in options.iter().filter(|o| o.kind == "select") {
            let wanted = if option.id == id {
                Some(value)
            } else if option.effort() {
                effort
            } else {
                self.config.choices.get(&option.id).map(String::as_str)
            };
            let selected = wanted.filter(|v| option.offered(v)).or_else(|| {
                option
                    .current_value
                    .as_deref()
                    .filter(|v| option.offered(v))
            });
            if let Some(selected) = selected {
                choices.insert(option.id.clone(), selected.into());
            }
        }
        if choices.get(id).map(String::as_str) != Some(value) {
            return Err("The model observation does not confirm its selected value.".into());
        }
        let mut selected_options = options.clone();
        for option in &mut selected_options {
            if let Some(value) = choices.get(&option.id) {
                option.current_value = Some(value.clone());
            }
        }
        self.config.options = Some(selected_options);
        self.config.choices = choices;
        Ok(())
    }
}
pub fn remembered(agent: &str, cx: &gpui_kit::App) -> Option<Value> {
    Preferences::value(PREFERENCE_KEY, cx)?.get(agent).cloned()
}
pub fn remember(agent: &str, config: &AgentConfig, cx: &mut gpui_kit::App) {
    let mut all = Preferences::value(PREFERENCE_KEY, cx)
        .filter(Value::is_object)
        .unwrap_or_else(|| json!({}));
    let values = preference_values(config);
    all[agent] = values;
    Preferences::save(PREFERENCE_KEY, all, cx);
}
fn preference_values(config: &AgentConfig) -> Value {
    let mut values = json!({});
    for (option, current) in config.menus() {
        if !option.offered(current) {
            continue;
        }
        if option.model() {
            values["model"] = json!(current);
        }
        if option.effort() {
            values["effort"] = json!(current);
        }
    }
    values
}

#[cfg(test)]
#[path = "draft_config_tests.rs"]
pub(crate) mod tests;

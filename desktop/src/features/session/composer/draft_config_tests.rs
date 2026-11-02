use super::{Catalogue, DraftConfig};
use serde_json::{Value, json};
fn options(model: &str, efforts: &[&str], current: &str) -> Value {
    json!([
      {"id":"provider-model","name":"Model 🦀","category":"model","type":"select","currentValue":model,
       "options":[{"value":"A","name":"A"},{"value":"B","name":"B"}]},
      {"id":"reasoning_effort","name":"Effort","category":"thought_level","type":"select","currentValue":current,
       "options":efforts.iter().map(|v|json!({"value":v,"name":v})).collect::<Vec<_>>()},
      {"id":"permission","name":"Access","type":"select","currentValue":"ask",
       "options":[{"value":"ask","name":"Ask"},{"value":"full","name":"Full"}]}])
}
pub(crate) fn catalogue_value() -> Value {
    json!({"options":options("A", &["low","high"], "high"),"choices":{},"commands":null,
      "modelOptions":{"A":options("A", &["low","high"], "high"),"B":options("B", &["minimal","medium"], "minimal")}})
}
fn catalogue() -> Catalogue {
    serde_json::from_value(catalogue_value()).unwrap()
}
#[test]
fn a_model_pick_uses_its_own_efforts_and_default_and_retains_offered_access() {
    let mut draft = DraftConfig::new(catalogue(), None).unwrap();
    draft.choose("permission", "full", None).unwrap();
    draft.choose("provider-model", "B", Some("high")).unwrap();
    assert_eq!(draft.config.choices["provider-model"], "B");
    assert_eq!(draft.config.choices["reasoning_effort"], "minimal");
    assert_eq!(draft.config.choices["permission"], "full");
    let effort = draft
        .config
        .options
        .as_ref()
        .unwrap()
        .iter()
        .find(|o| o.id == "reasoning_effort")
        .unwrap();
    assert_eq!(
        effort
            .options
            .iter()
            .map(|v| v.value.as_str())
            .collect::<Vec<_>>(),
        ["minimal", "medium"]
    );
}
#[test]
fn remembered_values_must_be_offered_and_access_starts_on_live_default() {
    let remembered = json!({"model":"B","effort":"medium","access":"full"});
    let draft = DraftConfig::new(catalogue(), Some(&remembered)).unwrap();
    assert_eq!(draft.config.choices["provider-model"], "B");
    assert_eq!(draft.config.choices["reasoning_effort"], "medium");
    assert_eq!(draft.config.choices["permission"], "ask");
    let draft = DraftConfig::new(
        catalogue(),
        Some(&json!({"model":"missing","effort":"invented"})),
    )
    .unwrap();
    assert_eq!(draft.config.choices["provider-model"], "A");
    assert_eq!(draft.config.choices["reasoning_effort"], "high");
}
#[test]
fn a_failed_model_observation_and_unknown_pick_never_fabricate_values() {
    let mut offered = catalogue();
    offered.model_options.remove("B");
    let mut draft = DraftConfig::new(offered, None).unwrap();
    let before = draft.config.clone();
    assert!(draft.choose("provider-model", "B", None).is_err());
    assert_eq!(draft.config, before);
    assert!(draft.choose("reasoning_effort", "invented", None).is_err());
    assert_eq!(draft.config, before);
}

#[test]
fn preference_payload_remembers_only_actual_model_and_effort_never_access() {
    let mut draft = DraftConfig::new(catalogue(), None).unwrap();
    draft.choose("provider-model", "B", None).unwrap();
    draft.choose("reasoning_effort", "medium", None).unwrap();
    draft.choose("permission", "full", None).unwrap();
    assert_eq!(
        super::preference_values(&draft.config),
        json!({"model":"B","effort":"medium"})
    );
}

#[test]
fn missing_provider_defaults_do_not_select_the_first_offered_effort_or_access() {
    let mut observed = catalogue_value();
    observed["modelOptions"]["A"][1]["currentValue"] = Value::Null;
    observed["modelOptions"]["A"][2]["currentValue"] = json!("not-offered");
    let mut draft = DraftConfig::new(serde_json::from_value(observed).unwrap(), None).unwrap();
    assert_eq!(draft.config.choices["provider-model"], "A");
    assert!(!draft.config.choices.contains_key("reasoning_effort"));
    assert!(!draft.config.choices.contains_key("permission"));
    assert_eq!(
        super::preference_values(&draft.config),
        json!({"model":"A"})
    );
    draft.choose("reasoning_effort", "low", None).unwrap();
    draft.choose("permission", "full", None).unwrap();
    assert_eq!(draft.config.choices["reasoning_effort"], "low");
    assert_eq!(draft.config.choices["permission"], "full");
    assert_eq!(
        super::preference_values(&draft.config),
        json!({"model":"A","effort":"low"})
    );
}

#[test]
fn no_model_is_invented_when_the_provider_current_model_is_missing() {
    let mut observed = catalogue_value();
    observed["options"][0]["currentValue"] = Value::Null;
    let unselected =
        DraftConfig::new(serde_json::from_value(observed.clone()).unwrap(), None).unwrap();
    assert!(!unselected.ready());
    assert!(!unselected.config.choices.contains_key("provider-model"));
    let draft = DraftConfig::new(
        serde_json::from_value(observed).unwrap(),
        Some(&json!({"model":"B"})),
    )
    .unwrap();
    assert_eq!(draft.config.choices["provider-model"], "B");
}

#[test]
fn draft_menus_show_validated_remembered_and_explicit_picks_and_actual_model_defaults() {
    let mut draft =
        DraftConfig::new(catalogue(), Some(&json!({"model":"B","effort":"medium"}))).unwrap();
    let selected = |draft: &DraftConfig, id: &str| {
        draft
            .config
            .menus()
            .into_iter()
            .find(|(option, _)| option.id == id)
            .unwrap()
            .1
            .to_string()
    };
    assert_eq!(selected(&draft, "provider-model"), "B");
    assert_eq!(selected(&draft, "reasoning_effort"), "medium");
    draft.choose("permission", "full", None).unwrap();
    draft.choose("reasoning_effort", "minimal", None).unwrap();
    assert_eq!(selected(&draft, "permission"), "full");
    assert_eq!(selected(&draft, "reasoning_effort"), "minimal");
    draft.choose("provider-model", "A", None).unwrap();
    assert_eq!(selected(&draft, "provider-model"), "A");
    assert_eq!(selected(&draft, "reasoning_effort"), "high");
    assert_eq!(selected(&draft, "permission"), "full");
    assert_eq!(
        super::preference_values(&draft.config),
        json!({"model":"A","effort":"high"})
    );
}

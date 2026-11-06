use super::Grouped;
use crate::features::session::composer::agent_config::{ConfigChoice, ConfigOption};

fn option(id: &str, category: Option<&str>, values: &[&str]) -> (ConfigOption, String) {
    (
        ConfigOption {
            id: id.into(),
            name: id.into(),
            category: category.map(Into::into),
            kind: "select".into(),
            current_value: values.first().map(|value| (*value).to_string()),
            options: values
                .iter()
                .map(|value| ConfigChoice {
                    value: (*value).into(),
                    name: value.to_uppercase(),
                    description: None,
                })
                .collect(),
        },
        values
            .first()
            .map_or_else(String::new, |value| (*value).to_string()),
    )
}

#[test]
fn every_option_the_provider_reports_lands_in_a_pill() {
    let grouped = Grouped::of(vec![
        option("provider-model", Some("model"), &["sonnet"]),
        option("reasoning_effort", None, &["high"]),
        option("provider-mode", Some("mode"), &["default"]),
        option("access", None, &["ask"]),
        option("second-mode", Some("mode"), &["plan"]),
    ]);
    assert_eq!(grouped.model.as_ref().unwrap().0.id, "provider-model");
    assert_eq!(grouped.effort.as_ref().unwrap().0.id, "reasoning_effort");
    assert_eq!(grouped.approval.as_ref().unwrap().0.id, "provider-mode");
    let others: Vec<_> = grouped
        .others
        .iter()
        .map(|(option, _)| option.id.as_str())
        .collect();
    assert_eq!(others, ["access", "second-mode"]);
}

#[test]
fn the_pill_reads_the_providers_model_and_the_effort_word() {
    let grouped = Grouped::of(vec![
        option("model", Some("model"), &["sonnet"]),
        option("effort", Some("effort"), &["xhigh"]),
    ]);
    assert_eq!(grouped.pill_label(), "SONNET · Extra High");
    assert_eq!(Grouped::of(Vec::new()).pill_label(), "Agent settings");
}

//! The panel's selected safe Engine projection. This retains no unrelated history and never
//! manufactures a provider row or adopts an operation from a rejected Start candidate.
use super::SignInTarget;
use serde::Deserialize;
use serde_json::Value;

#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(super) struct SignInChoice {
    pub method_id: String,
    pub name: String,
    pub kind: String,
}
#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(super) struct ProviderFacts {
    pub provider: String,
    pub name: String,
    pub adapter: String,
    pub version: Option<String>,
    pub installation: String,
    pub auth: String,
    pub problem: Option<String>,
    pub methods: Vec<SignInChoice>,
}
#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(super) struct ProviderRow {
    pub entity_kind: String,
    pub id: String,
    pub version: i64,
    pub machine: String,
    pub provider: String,
    pub account_context: String,
    pub facts_json: Option<String>,
}
impl ProviderRow {
    pub fn facts(&self) -> Option<ProviderFacts> {
        let facts: ProviderFacts = serde_json::from_str(self.facts_json.as_deref()?).ok()?;
        (facts.provider == self.provider).then_some(facts)
    }
}
#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(super) struct SignInRow {
    pub entity_kind: String,
    pub id: String,
    pub version: i64,
    pub machine: String,
    pub provider: String,
    pub account_context: String,
    pub method_id: Option<String>,
    pub state: String,
    pub reason: Option<String>,
}
impl SignInRow {
    pub fn active(&self) -> bool {
        matches!(
            self.state.as_str(),
            "starting" | "waiting_for_person" | "finishing"
        )
    }
}
pub(super) struct Projection {
    pub target: SignInTarget,
    pub provider: Option<ProviderRow>,
    pub operation_id: Option<String>,
    pub operation: Option<SignInRow>,
    pub ready: bool,
    snapshot_active: Option<SignInRow>,
}
impl Projection {
    pub fn new(target: SignInTarget) -> Self {
        Self {
            target,
            provider: None,
            operation_id: None,
            operation: None,
            ready: false,
            snapshot_active: None,
        }
    }
    pub fn begin_snapshot(&mut self) {
        self.ready = false;
        self.snapshot_active = None;
    }
    pub fn snapshot_item(&mut self, value: Value) {
        if value["entityKind"] == "provider" {
            self.install_provider(value);
            return;
        }
        let Ok(row) = serde_json::from_value::<SignInRow>(value) else {
            return;
        };
        if !self.matches_operation(&row) {
            return;
        }
        if self.operation_id.as_deref() == Some(&row.id) {
            self.set_operation(row.clone());
        }
        if row.active()
            && self
                .snapshot_active
                .as_ref()
                .is_none_or(|held| held.id == row.id && held.version < row.version)
        {
            self.snapshot_active = Some(row);
        }
    }
    pub fn finish_snapshot(&mut self) {
        if let Some(active) = self.snapshot_active.take()
            && self
                .operation
                .as_ref()
                .is_none_or(|row| !row.active() || row.id == active.id)
        {
            self.retain_operation(&active.id);
            self.set_operation(active);
        }
        self.ready = true;
    }
    pub fn retain_operation(&mut self, id: &str) {
        if self.operation_id.as_deref() != Some(id) {
            self.operation_id = Some(id.into());
            self.operation = None;
        }
    }
    pub fn install_provider(&mut self, value: Value) -> bool {
        let Ok(row) = serde_json::from_value::<ProviderRow>(value) else {
            return false;
        };
        if row.entity_kind != "provider"
            || row.id != self.target.provider_target_id
            || row.machine != self.target.machine
            || row.provider != self.target.provider
            || row.account_context != self.target.account_context
            || self
                .provider
                .as_ref()
                .is_some_and(|held| held.version > row.version)
        {
            return false;
        }
        self.provider = Some(row);
        true
    }
    pub fn install_operation(&mut self, value: Value) -> bool {
        let Ok(row) = serde_json::from_value::<SignInRow>(value) else {
            return false;
        };
        if !self.matches_operation(&row) || self.operation_id.as_deref() != Some(&row.id) {
            return false;
        }
        self.set_operation(row)
    }
    fn matches_operation(&self, row: &SignInRow) -> bool {
        row.entity_kind == "sign_in"
            && row.machine == self.target.machine
            && row.provider == self.target.provider
            && row.account_context == self.target.account_context
            && row.version >= 0
    }
    fn set_operation(&mut self, row: SignInRow) -> bool {
        if self
            .operation
            .as_ref()
            .is_some_and(|held| held.id == row.id && held.version > row.version)
        {
            return false;
        }
        self.operation = Some(row);
        true
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    fn target() -> super::super::SignInTarget {
        super::super::SignInTarget {
            machine: "owner".into(),
            provider: "codex".into(),
            account_context: "".into(),
            provider_target_id: "codex/".into(),
            configuration_target_id: "config-hash".into(),
            machine_label: "This Mac".into(),
            provider_name: "Codex".into(),
        }
    }
    fn row(id: &str, version: i64, state: &str) -> serde_json::Value {
        json!({"entityKind":"sign_in","id":id,"machine":"owner","version":version,"provider":"codex","accountContext":"","state":state,"methodId":null,"reason":null,"deadlineMs":null,"originNeedId":null})
    }
    #[test]
    fn active_operation_is_found_only_after_the_complete_paged_snapshot() {
        let mut projection = Projection::new(target());
        projection.begin_snapshot();
        projection.snapshot_item(row("actual-other-window", 2, "waiting_for_person"));
        let mut wrong = row("wrong-owner", 9, "starting");
        wrong["machine"] = json!("other");
        projection.snapshot_item(wrong);
        assert!(!projection.ready);
        assert!(projection.operation_id.is_none());
        projection.finish_snapshot();
        assert_eq!(
            projection.operation_id.as_deref(),
            Some("actual-other-window")
        );
        assert_eq!(projection.operation.as_ref().unwrap().version, 2);
    }
    #[test]
    fn entity_reads_cannot_regress_versions_or_adopt_another_target_or_operation() {
        let mut projection = Projection::new(target());
        projection.retain_operation("committed");
        assert!(projection.install_operation(row("committed", 4, "finishing")));
        assert!(!projection.install_operation(row("committed", 3, "waiting_for_person")));
        assert!(!projection.install_operation(row("rejected-candidate", 99, "starting")));
        let mut wrong = row("committed", 5, "signed_in");
        wrong["accountContext"] = json!("other");
        assert!(!projection.install_operation(wrong));
        assert_eq!(projection.operation.as_ref().unwrap().state, "finishing");
    }
}

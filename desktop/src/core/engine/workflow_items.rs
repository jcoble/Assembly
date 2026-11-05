//! Exact display rows from WorkflowReadModels. Sparse Engine events only invalidate these facts.
use super::{EngineKey, EngineRefusal};
use serde::{Deserialize, Deserializer};
use serde_json::Value;
#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
// The Engine's read model; Build Board (tsk-1656) reads these, so an expect would fail once it merges.
#[allow(dead_code)]
pub struct RunItem {
    entity_kind: String,
    pub id: String,
    pub machine: String,
    pub version: i64,
    pub state: String,
    pub title: Option<String>,
    pub request: Option<String>,
    pub project_id: Option<String>,
    pub project_root: Option<String>,
    pub definition_path: Option<String>,
    pub definition_hash: Option<String>,
    pub stage: Option<String>,
    pub owner_session_id: Option<String>,
    pub owner_epoch: Option<i64>,
    pub worktree: Option<String>,
    pub branch: Option<String>,
    pub pr_id: Option<String>,
    pub started_ms: Option<i64>,
    pub ended_ms: Option<i64>,
    pub paused: bool,
    pub reference: bool,
    pub active_step_ids: Vec<String>,
    pub display_prerequisite: Option<String>,
    pub task_url: Option<String>,
    pub rerun_of: Option<String>,
    pub updated_ms: Option<i64>,
    pub setup_state: Option<String>,
    pub cleanup_state: Option<String>,
    pub cleanup_reason: Option<String>,
}
#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
pub struct Dependency {
    pub id: String,
    pub optional: bool,
}
#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
pub struct StepItem {
    entity_kind: String,
    pub id: String,
    pub machine: String,
    pub version: i64,
    pub run_id: String,
    pub name: String,
    pub definition_key: Option<String>,
    pub kind: String,
    pub state: String,
    pub result: Option<String>,
    pub session_id: Option<String>,
    pub attempt: i64,
    pub max_attempts: i64,
    #[serde(deserialize_with = "sql_bool")]
    pub required: bool,
    #[serde(deserialize_with = "sql_bool")]
    pub rule_skippable: bool,
    pub generation: Option<i64>,
    pub round: Option<i64>,
    pub attempt_in_generation: Option<i64>,
    pub workspace_id: Option<String>,
    pub authority_machine: Option<String>,
    pub authority_session_id: Option<String>,
    pub authority_epoch: Option<i64>,
    pub label_present: String,
    pub label_past: String,
    pub stage: Option<String>,
    pub detail: Option<String>,
    pub explanation: Option<String>,
    pub progress: Option<f64>,
    pub badges: Vec<Value>,
    /// What the engine would accept for this step now: retry, skip or continue.
    #[serde(default)]
    pub recovery: Vec<String>,
    pub required_scope: Option<String>,
    pub scope_granted: bool,
    pub closed: bool,
    pub started_ms: Option<i64>,
    pub ended_ms: Option<i64>,
    pub outcome_reason: Option<String>,
    pub receipt_id: Option<String>,
    pub dispatch_state: Option<String>,
    pub deadline_ms: Option<i64>,
    pub needs_me_ids: Vec<String>,
    /// The engine's word that this step is stopped until the person moves it.
    #[serde(default)]
    pub needs_owner: bool,
    #[serde(default)]
    pub owner_session_id: Option<String>,
    pub dependencies: Vec<Dependency>,
}
// The SQL display currently emits required/ruleSkippable as 0/1, while explicitly
// JSON-typed scopeGranted/closed are booleans. Refuse every other representation.
fn sql_bool<'de, D: Deserializer<'de>>(d: D) -> Result<bool, D::Error> {
    match Value::deserialize(d)? {
        Value::Bool(value) => Ok(value),
        Value::Number(value) if value.as_i64() == Some(0) => Ok(false),
        Value::Number(value) if value.as_i64() == Some(1) => Ok(true),
        _ => Err(serde::de::Error::custom("invalid SQL boolean")),
    }
}
fn invalid(message: &str) -> EngineRefusal {
    EngineRefusal {
        code: "invalid_workflow_display".into(),
        message: message.into(),
    }
}
fn identity(
    kind: &str,
    id: &str,
    machine: &str,
    version: i64,
    expected: &str,
) -> Result<(), EngineRefusal> {
    if kind != expected || id.is_empty() || machine.is_empty() || version < 0 {
        Err(invalid("Invalid workflow display identity."))
    } else {
        Ok(())
    }
}
fn bounded(value: &Value) -> Result<(), EngineRefusal> {
    if serde_json::to_vec(value)
        .map_err(|_| invalid("Invalid workflow JSON."))?
        .len()
        > 32768
    {
        Err(invalid("Workflow display item exceeds 32 KiB."))
    } else {
        Ok(())
    }
}
impl RunItem {
    pub fn key(&self) -> EngineKey {
        EngineKey {
            machine: self.machine.clone(),
            kind: "run".into(),
            id: self.id.clone(),
        }
    }
    pub fn decode(value: Value) -> Result<Self, EngineRefusal> {
        bounded(&value)?;
        let row: Self = serde_json::from_value(value)
            .map_err(|_| invalid("Incomplete workflow run display."))?;
        identity(&row.entity_kind, &row.id, &row.machine, row.version, "run")?;
        Ok(row)
    }
}
impl StepItem {
    #[cfg(test)]
    pub fn key(&self) -> EngineKey {
        EngineKey {
            machine: self.machine.clone(),
            kind: "step".into(),
            id: self.id.clone(),
        }
    }
    pub fn decode(value: Value) -> Result<Self, EngineRefusal> {
        bounded(&value)?;
        let row: Self = serde_json::from_value(value)
            .map_err(|_| invalid("Incomplete workflow step display."))?;
        identity(&row.entity_kind, &row.id, &row.machine, row.version, "step")?;
        Ok(row)
    }
}
#[derive(Clone, Debug)]
pub struct RunView {
    pub run: RunItem,
    pub steps: Vec<StepItem>,
    pub next: Option<String>,
}
impl RunView {
    pub fn decode(value: Value, run_id: &str) -> Result<Self, EngineRefusal> {
        if serde_json::to_vec(&value)
            .map_err(|_| invalid("Invalid workflow page."))?
            .len()
            > 1024 * 1024
        {
            return Err(invalid("Workflow page exceeds 1 MiB."));
        }
        let run = RunItem::decode(value["run"].clone())?;
        if run.id != run_id {
            return Err(invalid("Workflow page names another run."));
        }
        let rows = value["steps"]
            .as_array()
            .filter(|rows| rows.len() <= 200)
            .ok_or_else(|| invalid("Invalid workflow step page."))?;
        let mut ids = std::collections::HashSet::new();
        let mut steps = Vec::with_capacity(rows.len());
        for value in rows {
            let step = StepItem::decode(value.clone())?;
            if step.run_id != run.id || !ids.insert(step.id.clone()) {
                return Err(invalid("Unrequested or duplicate workflow step."));
            }
            steps.push(step);
        }
        let next = match value.get("next") {
            None | Some(Value::Null) => None,
            Some(Value::String(token)) if !token.is_empty() => Some(token.clone()),
            _ => return Err(invalid("Invalid workflow continuation.")),
        };
        Ok(Self { run, steps, next })
    }
}

/// One read owns its full ordered collection; no partial page is installed in a view.
pub struct RunPages {
    view: RunView,
    tokens: std::collections::HashSet<String>,
    ids: std::collections::HashSet<String>,
}
impl RunPages {
    pub fn new(view: RunView) -> Result<Self, EngineRefusal> {
        if view.next.is_some() && view.steps.is_empty() {
            return Err(invalid("Workflow continuation made no progress."));
        }
        let tokens = view.next.iter().cloned().collect();
        let ids = view.steps.iter().map(|step| step.id.clone()).collect();
        Ok(Self { view, tokens, ids })
    }
    pub fn view(&self) -> &RunView {
        &self.view
    }
    pub fn finish(self) -> RunView {
        self.view
    }
    pub fn append(&mut self, page: RunView) -> Result<(), EngineRefusal> {
        if self.view.next.is_none()
            || page.run.key() != self.view.run.key()
            || page.run.definition_hash != self.view.run.definition_hash
            || page.run.version != self.view.run.version
        {
            return Err(invalid("The workflow changed during its paged read."));
        }
        if page
            .next
            .as_ref()
            .is_some_and(|next| self.tokens.contains(next))
            || page.next.is_some() && page.steps.is_empty()
            || page.steps.iter().any(|step| self.ids.contains(&step.id))
        {
            return Err(invalid("Workflow continuation did not progress."));
        }
        if let Some(token) = &page.next {
            self.tokens.insert(token.clone());
        }
        self.ids
            .extend(page.steps.iter().map(|step| step.id.clone()));
        self.view.steps.extend(page.steps);
        self.view.next = page.next;
        Ok(())
    }
}

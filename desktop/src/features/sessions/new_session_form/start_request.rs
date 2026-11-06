use crate::core::socket::row_source::ExpectedRowSource;
use crate::features::projects::projects::ProjectRecord;
use serde::Serialize;
use serde_json::{Value, json};

/// The existing Application CheckoutIntent wire, captured as an unsent choice.
#[derive(Clone, Debug, PartialEq, Serialize)]
#[serde(tag = "kind", rename_all = "lowercase")]
#[expect(
    dead_code,
    reason = "The Application accepts all three; the form offers only the current checkout so far."
)]
pub enum CheckoutIntent {
    Current,
    Branch { branch: String },
    Worktree { base: String },
}
/// One immutable create identity. Unknown replies retain this exact request before any explicit retry.
#[derive(Clone)]
pub struct StartRequest {
    client_id: String,
    agent: String,
    project: Option<ProjectRecord>,
    machine: Option<String>,
    checkout: Option<CheckoutIntent>,
    expected_source: ExpectedRowSource,
    initial_config: Value,
}
impl StartRequest {
    pub fn capture(
        agent: String,
        project: Option<ProjectRecord>,
        machine: Option<String>,
        checkout: Option<CheckoutIntent>,
        expected_source: ExpectedRowSource,
        initial_config: Value,
    ) -> Result<Self, String> {
        if agent.is_empty() {
            return Err("Choose an advertised agent.".into());
        }
        if expected_source.machine.is_empty()
            || expected_source.route_machine.as_deref() != machine.as_deref()
            || (machine.is_some() && expected_source.generation == 0)
            || (machine.is_none() && expected_source.generation != 0)
        {
            return Err("The original source does not match this draft's machine route.".into());
        }
        if let Some(project) = &project {
            let route = if project.machine == "local" {
                None
            } else {
                Some(project.machine.as_str())
            };
            if project.removed_at.is_some() || project.id.is_empty() || route != machine.as_deref()
            {
                return Err("The chosen project or machine changed.".into());
            }
        } else if checkout.is_some() {
            return Err("No project does not accept a checkout choice.".into());
        }
        Ok(Self {
            client_id: uuid::Uuid::new_v4().to_string(),
            agent,
            project,
            machine,
            checkout,
            expected_source,
            initial_config,
        })
    }
    pub fn source(&self) -> &ExpectedRowSource {
        &self.expected_source
    }
    pub fn project_id(&self) -> Option<&str> {
        self.project.as_ref().map(|project| project.id.as_str())
    }
    pub fn requires_checkout(&self) -> bool {
        self.checkout.is_some()
    }
    pub fn is_current(&self) -> bool {
        matches!(self.checkout, Some(CheckoutIntent::Current))
    }
    pub fn client_id(&self) -> &str {
        &self.client_id
    }
    pub fn machine(&self) -> Option<&str> {
        self.machine.as_deref()
    }
    pub fn arguments(&self) -> Value {
        json!({"agent":self.agent,"clientId":self.client_id,"projectId":self.project.as_ref().map(|p|p.id.as_str()),
            "machine":self.machine,"checkout":self.checkout,"expectedSource":self.expected_source,"initialConfig":self.initial_config})
    }
    pub fn by_client_arguments(&self) -> Value {
        json!({"clientId":self.client_id,"machine":self.machine,"expectedSource":self.expected_source})
    }
}
#[cfg(test)]
#[path = "start_request_tests.rs"]
mod tests;

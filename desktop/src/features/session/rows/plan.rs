//! `PlanItem` is one step of the agent's plan; the newest plan row's items show in the plan card.

use serde::Deserialize;

#[derive(Clone, Debug, PartialEq, Deserialize)]
pub struct PlanItem {
    pub text: String,
    pub status: PlanStatus,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum PlanStatus {
    Pending,
    InProgress,
    Completed,
}

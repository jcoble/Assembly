use mcb_core::broker::{Envelope, MessageKind, Receipt};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum AgentWorkStatus {
    Running,
    Waiting,
    Blocked,
    Idle,
    Done,
    Failed,
}

impl AgentWorkStatus {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Running => "running",
            Self::Waiting => "waiting",
            Self::Blocked => "blocked",
            Self::Idle => "idle",
            Self::Done => "done",
            Self::Failed => "failed",
        }
    }
}

pub fn status_for(
    turn_active: bool,
    awaiting_permission_or_input: bool,
    decision_pending: bool,
    runtime_error: bool,
    session_closed: bool,
) -> AgentWorkStatus {
    if runtime_error {
        AgentWorkStatus::Failed
    } else if session_closed {
        AgentWorkStatus::Done
    } else if decision_pending {
        AgentWorkStatus::Blocked
    } else if awaiting_permission_or_input {
        AgentWorkStatus::Waiting
    } else if turn_active {
        AgentWorkStatus::Running
    } else {
        AgentWorkStatus::Idle
    }
}

#[derive(Clone, Debug, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct WorkflowBrokerEvent {
    pub group_id: String,
    pub envelope: BrokerEnvelope,
}

#[derive(Clone, Debug, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BrokerEnvelope {
    id: String,
    group_id: String,
    from_agent: String,
    to_agent: String,
    kind: &'static str,
    body: String,
    receipt: &'static str,
    created_at_ms: i64,
    updated_at_ms: i64,
}

impl From<Envelope> for BrokerEnvelope {
    fn from(envelope: Envelope) -> Self {
        Self {
            id: envelope.id,
            group_id: envelope.group_id,
            from_agent: envelope.from_agent,
            to_agent: envelope.to_agent,
            kind: match envelope.kind {
                MessageKind::Message => "message",
                MessageKind::Status => "status",
                MessageKind::DecisionRequest => "decisionRequest",
                MessageKind::DecisionResponse => "decisionResponse",
                MessageKind::System => "system",
            },
            body: envelope.body,
            receipt: match envelope.receipt {
                Receipt::Queued => "queued",
                Receipt::Delivered => "delivered",
                Receipt::Failed => "failed",
                Receipt::Expired => "expired",
            },
            created_at_ms: envelope.created_at_ms,
            updated_at_ms: envelope.updated_at_ms,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::{status_for, AgentWorkStatus};

    #[test]
    fn status_mapping_covers_all_inputs() {
        let cases = [
            ((true, true, true, true, true), AgentWorkStatus::Failed),
            ((true, true, true, false, true), AgentWorkStatus::Done),
            ((true, true, true, false, false), AgentWorkStatus::Blocked),
            ((true, true, false, false, false), AgentWorkStatus::Waiting),
            ((true, false, false, false, false), AgentWorkStatus::Running),
            ((false, false, false, false, false), AgentWorkStatus::Idle),
        ];

        for (
            (
                turn_active,
                awaiting_permission_or_input,
                decision_pending,
                runtime_error,
                session_closed,
            ),
            expected,
        ) in cases
        {
            assert_eq!(
                status_for(
                    turn_active,
                    awaiting_permission_or_input,
                    decision_pending,
                    runtime_error,
                    session_closed,
                ),
                expected
            );
        }
    }
}

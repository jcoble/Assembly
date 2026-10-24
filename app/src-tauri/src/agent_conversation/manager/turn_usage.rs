use super::*;
use crate::usage_db::acp::AcpTurn;

/// Receives each finished turn's ACP usage. The app stores it as usage rows.
pub type TurnUsageListener = Arc<dyn Fn(AcpTurn) + Send + Sync>;

static LISTENER: std::sync::OnceLock<TurnUsageListener> = std::sync::OnceLock::new();

/// What a session remembers about its running turn for the usage rows.
#[derive(Default)]
pub(super) struct TurnUsageState {
    started_at_micros: Option<i64>,
    cost_total_usd: Option<f64>,
    /// The session was loaded or resumed when it last attached, not created.
    pub(super) resumed: bool,
}

fn now_micros() -> i64 {
    (timestamp_millis() * 1000) as i64
}

/// Marks when a prompt goes out, so the turn's rows say when the turn began.
pub(super) fn turn_started(session: &mut ManagedAgentSession) {
    session.turn_usage.started_at_micros = Some(now_micros());
}

pub fn set_turn_usage_listener(listener: TurnUsageListener) {
    let _ = LISTENER.set(listener);
}

/// Keeps the running cost total a `usage_update` reports, for the turn's rows.
pub(super) fn note_reported_cost(session: &mut ManagedAgentSession, params: &Value) {
    if let Some(amount) = super::super::normalization::turn_usage::reported_cost_total(params) {
        session.turn_usage.cost_total_usd = Some(amount);
    }
}

/// Hands the `session/prompt` result (null when the turn failed) and the latest reported cost to the listener.
pub(super) fn report_turn_usage(session: &mut ManagedAgentSession, turn_id: &str, result: &Value) {
    let started_at_micros = session.turn_usage.started_at_micros.take();
    let cost_total_usd = session.turn_usage.cost_total_usd.take();
    let Some(listener) = LISTENER.get() else { return };
    listener(AcpTurn {
        owned_id: session.owned_id.clone(),
        native_session_id: session.native_session_id.clone(),
        provider: session.provider.id().to_string(),
        turn_id: turn_id.to_string(),
        models: super::super::normalization::turn_usage::turn_model_tokens(result, session.config.model.as_deref()),
        cost_total_usd,
        node_run_id: None,
        workflow_id: None,
        resumed: session.turn_usage.resumed,
        occurred_at_micros: started_at_micros.unwrap_or_else(now_micros),
        cwd: Some(session.cwd.clone()),
    });
}

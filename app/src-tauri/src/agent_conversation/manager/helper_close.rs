use super::*;

pub(crate) const HELPER_CLOSE_AFTER_MS: i64 = 60 * 60 * 1000;
const SWEEP_EVERY: std::time::Duration = std::time::Duration::from_secs(300);

impl AgentRuntimeManager {
    /// Marks eligible helpers done using this backend's current app setting.
    pub(crate) fn close_finished_helpers(&self, now_ms: i64) -> Result<usize, String> {
        let minutes = self.read_app_setting("helper_auto_done_minutes")?
            .and_then(|value| serde_json::from_str::<i64>(&value).ok())
            .filter(|minutes| *minutes >= 0).unwrap_or(HELPER_CLOSE_AFTER_MS / 60_000);
        if minutes == 0 { return Ok(0); }
        let quiet_ms = minutes.saturating_mul(60_000);
        let due = self.store.finished_helpers_due(now_ms, quiet_ms)
            .map_err(|error| error.to_string())?;
        let mut count = 0;
        for owned_id in due {
            let result = (|| -> Result<bool, String> {
                self.hydrate_overlay_from_store(&owned_id)?;
                let mut sessions = self.sessions.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
                let Some(session) = sessions.get_mut(&owned_id) else { return Ok(false) };
                // The candidate may have received work while we waited for this lock.
                if store_timestamp(session.last_activity_ms) > now_ms.saturating_sub(quiet_ms)
                    || session.rail_meta.completed_at.is_some()
                    || (session.state == AgentRuntimeState::Ready && !session_is_quiescent(session))
                    || matches!(session.state, AgentRuntimeState::Starting | AgentRuntimeState::Working
                        | AgentRuntimeState::Interrupting | AgentRuntimeState::WaitingApproval | AgentRuntimeState::WaitingInput)
                { return Ok(false); }
                let completed_at = chrono::Utc::now().to_rfc3339_opts(chrono::SecondsFormat::Millis, true);
                session.rail_meta.completed_at = Some(completed_at.clone());
                if let Err(error) = record_payload_for_session_and_dispatch(session, &self.emitter,
                    AgentConversationPayload::SessionCompleted { completed_at }) {
                    session.rail_meta.completed_at = None;
                    return Err(error);
                }
                Ok(true)
            })();
            match result {
                Ok(true) => count += 1,
                Ok(false) => {},
                Err(error) => crate::debug_log::stderr_log!("Could not close finished helper {owned_id}: {error}"),
            }
        }
        Ok(count)
    }

    /// Each backend starts one process-owned sweep, first running five minutes after startup.
    pub fn start_closing_finished_helpers(&self) {
        let manager = self.clone();
        tauri::async_runtime::spawn(async move {
            let mut interval = tokio::time::interval_at(tokio::time::Instant::now() + SWEEP_EVERY, SWEEP_EVERY);
            loop {
                interval.tick().await;
                if let Err(error) = manager.close_finished_helpers(store_timestamp(timestamp_millis())) {
                    crate::debug_log::stderr_log!("Could not find finished helpers: {error}");
                }
            }
        });
    }
}

use super::*;

impl AgentRuntimeManager {
    /// The Assembly session a provider session id belongs to. Telemetry carries
    /// only the provider's id. Live sessions are checked first, which covers a
    /// Claude session's own child sessions; a session suspended since its last
    /// export is found in the store.
    pub fn owned_id_for_native_session(&self, native_session_id: &str) -> Option<String> {
        let live = self.sessions.lock().unwrap_or_else(std::sync::PoisonError::into_inner).values()
            .find(|session| session.native_session_id.as_deref() == Some(native_session_id)
                || session.claude_children.contains_key(native_session_id))
            .map(|session| session.owned_id.clone());
        live.or_else(|| self.store.owned_id_for_native_session(native_session_id).ok().flatten())
    }

    /// The workflow run and step attempt an Assembly session ran, read from the
    /// durable workflow events so a retried step's old session is still found.
    pub fn run_and_step_for_session(&self, owned_id: &str) -> Option<(String, String)> {
        self.store.run_and_step_for_session(owned_id).ok().flatten()
    }
}

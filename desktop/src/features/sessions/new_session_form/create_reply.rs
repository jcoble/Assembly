use serde::Deserialize;
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CreateReply {
    #[serde(deserialize_with = "required_nullable")]
    pub session_id: Option<String>,
    #[serde(deserialize_with = "required_nullable")]
    pub project_id: Option<String>,
    /// Certified INITIAL checkout operation from the owning producer, never a later current operation.
    #[serde(deserialize_with = "required_nullable")]
    pub checkout_operation_id: Option<String>,
    pub checkout_state: Option<String>,
    pub state: Option<String>,
}
#[derive(Clone)]
pub struct AcceptedCreate {
    pub session_id: String,
    pub project_id: Option<String>,
    pub initial_operation_id: Option<String>,
}
impl AcceptedCreate {
    pub fn matches_project(&self, expected: Option<&str>) -> bool {
        self.project_id.as_deref() == expected
    }
}
impl CreateReply {
    /// A lookup with no accepted session is distinct from an accepted session whose proof is unavailable.
    pub fn accepted(&self, requires_checkout: bool) -> Result<Option<AcceptedCreate>, String> {
        let Some(session) = self.session_id.as_deref() else {
            if self.project_id.is_some()
                || self.checkout_operation_id.is_some()
                || self.checkout_state.is_some()
                || self.state.is_some()
            {
                return Err("The create reconciliation answer is inconsistent.".into());
            }
            return Ok(None);
        };
        if session.is_empty() {
            return Err("The create answer omitted its accepted session identity.".into());
        }
        if self
            .checkout_state
            .as_deref()
            .is_some_and(|state| !matches!(state, "ready" | "pending" | "refused"))
        {
            return Err("The checkout outcome is unavailable.".into());
        }
        if self
            .checkout_operation_id
            .as_deref()
            .is_some_and(|id| uuid::Uuid::parse_str(id).is_err())
        {
            return Err("The initial checkout operation binding is invalid.".into());
        }
        if requires_checkout && self.checkout_operation_id.is_none() {
            return Err("The accepted session's initial checkout proof is unavailable. The draft was retained.".into());
        }
        Ok(Some(AcceptedCreate {
            session_id: session.into(),
            project_id: self.project_id.clone(),
            initial_operation_id: self.checkout_operation_id.clone(),
        }))
    }
}
fn required_nullable<'de, D: serde::Deserializer<'de>>(
    value: D,
) -> Result<Option<String>, D::Error> {
    Option::<String>::deserialize(value)
}
#[cfg(test)]
#[path = "create_reply_tests.rs"]
mod tests;

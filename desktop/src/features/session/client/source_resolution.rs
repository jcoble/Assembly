//! Exact current online route discovery. Its result is an assertion for open_target, never historical provenance.
use crate::core::{engine::EngineRefusal, socket::row_source::ExpectedRowSource};
use serde_json::{Value, json};
pub(crate) fn build_args(route: Option<&str>) -> Result<Value, EngineRefusal> {
    if route.is_some_and(|route| route.trim().is_empty()) {
        return Err(refused("An empty route is not a local source."));
    }
    Ok(json!({"routeMachine":route}))
}
pub(crate) fn parse_reply(
    value: Value,
    route: Option<&str>,
    local: Option<&str>,
) -> Result<ExpectedRowSource, EngineRefusal> {
    if value.get("routeMachine").is_none() {
        return Err(refused("The resolved source omitted its route assertion."));
    }
    let source: ExpectedRowSource =
        serde_json::from_value(value).map_err(|error| refused(&error.to_string()))?;
    let valid = if let Some(route) = route {
        !route.trim().is_empty()
            && source.generation > 0
            && source.route_machine.as_deref() == Some(route)
            && Some(source.machine.as_str()) != local
    } else {
        source.generation == 0
            && source.route_machine.is_none()
            && Some(source.machine.as_str()) == local
    };
    if !valid || source.machine.is_empty() {
        return Err(refused(
            "The resolved route or actual owning Engine changed.",
        ));
    }
    Ok(source)
}
fn refused(message: &str) -> EngineRefusal {
    EngineRefusal {
        code: "source_changed".into(),
        message: message.into(),
    }
}

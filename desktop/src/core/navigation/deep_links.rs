//! Carries out a link the app accepted, whether it came from the system or was clicked inside the app.

use gpui_kit::*;

use super::external_urls::ExternalRoute;

/// Carries out a route.
pub fn handle_route(route: ExternalRoute, cx: &mut App) {
    crate::features::tasks::tasks_pane::handle_external(route, cx);
}

/// Carries out an `assembly://` address clicked inside the app. False when it isn't one the app accepts.
pub fn handle_address(_address: &str, _cx: &mut App) -> bool {
    false
}

/// The notices this module showed, oldest first, for its tests.
#[cfg(test)]
pub fn notices(_cx: &App) -> Vec<String> {
    Vec::new()
}

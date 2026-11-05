//! Carries out a link the app accepted, whether it came from the system or was clicked inside the app.

use gpui_kit::component::WindowExt as _;
use gpui_kit::component::notification::Notification;
use gpui_kit::*;

use super::external_urls::ExternalRoute;
use super::waiting_session_link;

/// Carries out a route.
pub fn handle_route(route: ExternalRoute, cx: &mut App) {
    match route {
        ExternalRoute::Session { id, machine } => waiting_session_link::open(id, machine, cx),
        route => crate::features::tasks::tasks_pane::handle_external(route, cx),
    }
}

/// Carries out an `assembly://` address clicked inside the app. False when it isn't one the app accepts.
pub fn handle_address(_address: &str, _cx: &mut App) -> bool {
    false
}

/// A warning in the window, outside any window update.
pub(super) fn show_notice(text: String, cx: &mut App) {
    #[cfg(test)]
    cx.default_global::<Shown>().0.push(text.clone());
    if let Some(window) = cx
        .active_window()
        .or_else(|| cx.windows().into_iter().next())
    {
        let _ = window.update(cx, |_, window, cx| {
            window.push_notification(Notification::warning(text), cx);
        });
    }
}

#[cfg(test)]
#[derive(Default)]
struct Shown(Vec<String>);

#[cfg(test)]
impl Global for Shown {}

/// The notices this module showed, oldest first, for its tests.
#[cfg(test)]
pub fn notices(cx: &App) -> Vec<String> {
    cx.try_global::<Shown>()
        .map_or_else(Vec::new, |shown| shown.0.clone())
}

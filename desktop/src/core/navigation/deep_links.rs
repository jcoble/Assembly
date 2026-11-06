//! Carries out a link the app accepted, whether it came from the system or was clicked inside the app.

use gpui_kit::component::WindowExt as _;
use gpui_kit::component::notification::Notification;
use gpui_kit::*;

use super::external_urls::{ExternalRoute, parse_external};
use super::file_link;
use super::waiting_session_link;
use crate::core::intents::open_url::OpenUrl;
use crate::core::state::selected_session::SelectedSession;
use crate::features::tasks::tasks_pane::{self, NOT_OPENABLE};

/// Carries out a route.
pub fn handle_route(route: ExternalRoute, cx: &mut App) {
    match route {
        ExternalRoute::Session { id, machine } => waiting_session_link::open(id, machine, cx),
        ExternalRoute::Open { url } => cx.dispatch_action(&OpenUrl { url }),
        ExternalRoute::File { path, machine } => {
            let selected = SelectedSession::global(cx).read(cx);
            let from = selected.key().cloned().zip(selected.source().cloned());
            file_link::open(path, machine, from, (None, None), cx)
        }
        ExternalRoute::NotionOAuth { state, claim_code } => {
            tasks_pane::receive_sign_in(state, claim_code, cx)
        }
        ExternalRoute::Task { page_id } => tasks_pane::open_task_link(page_id, cx),
        ExternalRoute::NotFullPath => show_notice(file_link::NOT_FULL_PATH.into(), cx),
        ExternalRoute::Unsupported => show_notice(NOT_OPENABLE.into(), cx),
    }
}

/// Carries out an `assembly://` address clicked inside the app. False when it isn't one the app accepts. A click runs
/// while its window is being updated, and a route updates the window itself, so the route waits until that's done.
pub fn handle_address(address: &str, cx: &mut App) -> bool {
    let route = parse_external(address);
    if route == ExternalRoute::Unsupported {
        return false;
    }
    cx.defer(move |cx| handle_route(route, cx));
    true
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

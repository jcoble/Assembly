//! `Header` is the 48 px line above the timeline, as the approved look's title row: a button that shows and hides the
//! sessions column, a rule, then "project › Session title", and at the right the agent and machine and, for a session
//! another session started, a link to the session that started it (the open session intent, lane g0-host). The center
//! draws no tab row over a lone pane, so this button stands in for the one that row had. It follows the `sessions`
//! topic keyed on the open session. Replaces SessionHeader.svelte.

use gpui_kit::assets::IconName;
use gpui_kit::component::dock::DockPlacement;
use gpui_kit::component::tooltip::Tooltip;
use gpui_kit::component::{ActiveTheme as _, Icon, h_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use crate::core::intents::open_session::OpenSession;
use crate::core::socket::client::SocketClient;
use crate::core::socket::update::Update;
use crate::core::state::session_item::SessionItem;
use crate::core::ui::center_panes::CenterPanes;

/// The project a session is in: its folder's or repo's last name ("Assembly" for `https://github.com/me/Assembly`).
pub fn project(repo_url: &str) -> &str {
    let url = repo_url.trim_end_matches(['/', '\\']);
    let url = url.strip_suffix(".git").unwrap_or(url);
    url.rsplit(['/', '\\']).next().unwrap_or(url)
}

#[derive(Default)]
pub struct Header {
    /// The open session, once the topic has sent it.
    item: Option<SessionItem>,
    /// Owns the keyed `sessions` subscription; dropping it unsubscribes.
    _feed: Option<Task<()>>,
}

impl Header {
    /// The open session's machine, once the topic has sent it; `None` for this one.
    pub fn machine(&self) -> Option<&str> {
        (self.item.as_ref()?.machine.as_deref()).filter(|machine| !machine.is_empty())
    }

    /// Follows `session`, or shows nothing.
    pub fn show(&mut self, session: Option<String>, cx: &mut Context<Self>) {
        (self.item, self._feed) = (None, None);
        cx.notify();
        let (Some(session), Some(socket)) = (session, SocketClient::try_global(cx)) else {
            return;
        };
        let mut topic = socket.read(cx).subscribe("sessions", Some(&session));
        self._feed = Some(cx.spawn(async move |header, cx| {
            while let Ok(update) = topic.updates.recv().await {
                let item = match update {
                    Update::State(items) => items.into_iter().next().map(|(_, item)| item),
                    Update::Change { item, .. } => Some(item),
                };
                let Some(item) = item.and_then(read) else {
                    continue;
                };
                let shown = header.update(cx, |header, cx| {
                    header.item = Some(item);
                    cx.notify();
                });
                if shown.is_err() {
                    return;
                }
            }
        }));
    }
}

fn read(item: serde_json::Value) -> Option<SessionItem> {
    serde_json::from_value(item)
        .inspect_err(|error| eprintln!("header: a session that doesn't read: {error}"))
        .ok()
}

impl Render for Header {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let Some(item) = &self.item else {
            return div();
        };
        let theme = cx.theme();
        let (muted, border) = (theme.muted_foreground, theme.border);
        let parent = item.parent_session_id.clone();
        let sessions = div()
            .id("toggle-sessions")
            .size(px(28.))
            .flex()
            .flex_none()
            .items_center()
            .justify_center()
            .rounded(theme.radius)
            .text_color(theme.foreground.opacity(0.85))
            .cursor_pointer()
            .hover(|button| button.bg(theme.secondary_hover))
            .tooltip(|window, cx| Tooltip::new("Sessions").build(window, cx))
            .on_click(|_, window, cx| {
                if let Some(dock) = CenterPanes::dock(cx) {
                    dock.update(cx, |dock, cx| {
                        dock.toggle_dock(DockPlacement::Left, window, cx)
                    });
                }
            })
            .child(Icon::new(IconName::PanelLeft).size(px(14.)));
        div().child(
            h_flex()
                .h(px(48.))
                .gap(px(10.))
                .px(px(14.))
                .border_b_1()
                .border_color(border)
                .text_size(px(13.5))
                .child(sessions)
                .child(div().flex_none().w_px().h(px(16.)).bg(border))
                .child(
                    h_flex()
                        .flex_1()
                        .min_w_0()
                        .gap_1()
                        .text_color(muted)
                        .child(div().flex_none().child(project(&item.repo_url).to_string()))
                        .child(Icon::new(IconName::ChevronRight).size(px(14.)))
                        .child(
                            div()
                                .min_w_0()
                                .truncate()
                                .text_color(theme.foreground)
                                .font_weight(FontWeight::MEDIUM)
                                .child(item.title()),
                        ),
                )
                .child(
                    h_flex()
                        .flex_none()
                        .gap_1()
                        .text_xs()
                        .text_color(muted)
                        .child(item.place())
                        .when_some(parent, |line, parent| {
                            line.child("·").child(
                                div()
                                    .id("parent-session")
                                    .text_color(theme.link)
                                    .cursor_pointer()
                                    .child("Parent session")
                                    .on_click(move |_, window, cx| {
                                        let id = parent.clone();
                                        window.dispatch_action(Box::new(OpenSession { id }), cx)
                                    }),
                            )
                        }),
                ),
        )
    }
}

#[cfg(test)]
#[path = "header_tests.rs"]
mod tests;

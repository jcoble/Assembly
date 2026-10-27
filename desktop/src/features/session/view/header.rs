//! `Header` is the line above the timeline: the session's title, its agent and machine, and, for a session another
//! session started, a link to the session that started it (the open session intent, lane g0-host). It follows the
//! `sessions` topic keyed on the open session. Replaces SessionHeader.svelte.

use gpui_kit::component::{ActiveTheme as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use crate::core::intents::open_session::OpenSession;
use crate::core::socket::client::SocketClient;
use crate::core::socket::update::Update;
use crate::core::state::session_item::SessionItem;

#[derive(Default)]
pub struct Header {
    /// The open session, once the topic has sent it.
    item: Option<SessionItem>,
    /// Owns the keyed `sessions` subscription; dropping it unsubscribes.
    _feed: Option<Task<()>>,
}

impl Header {
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
        let parent = item.parent_session_id.clone();
        div().child(
            v_flex()
                .px_4()
                .py_2()
                .border_b_1()
                .border_color(theme.border)
                .child(
                    div()
                        .text_sm()
                        .font_weight(FontWeight::SEMIBOLD)
                        .truncate()
                        .child(item.title()),
                )
                .child(
                    h_flex()
                        .gap_1()
                        .text_xs()
                        .text_color(theme.muted_foreground)
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

//! `Header` is the 46 px crumb above the timeline, as the look's `.crumb` draws it: a button that shows and hides the
//! sessions column, then "project › Session title", and at the right the provider (and any other machine) in a quiet
//! pill and, for a session another session started, a link to the session that started it (the open session intent,
//! lane g0-host). The center draws no tab row over a lone pane, so this button stands in for the one that row had.
//! Built from the kit's Button, Tag and Icon. It follows the `sessions` topic keyed on the open session. Replaces
//! SessionHeader.svelte.

use crate::core::theme::look::Look;
use crate::core::theme::metrics::{BUTTON_HEIGHT, CORNER_PILL, CRUMB_HEIGHT, SPACE};
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::core::ui::access::Control as _;
use crate::core::ui::access::control_ids::chat as ids;
use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::dock::DockPlacement;
use gpui_kit::component::tag::Tag;
use gpui_kit::component::{Icon, h_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use crate::core::socket::client::SocketClient;
use crate::core::socket::update::Update;
use crate::core::state::session_item::{NO_PROJECT, SessionItem};
use crate::core::ui::center_panes::CenterPanes;
use crate::core::{
    engine::SessionKey,
    intents::session_open::{SessionOpenRequest, SessionOpenTarget},
};

/// The crumb's icons: the sessions button's and the chevron.
const CRUMB_ICON: Pixels = px(16.);
/// The provider pill's height, the look's `.runpill`.
const PILL_HEIGHT: Pixels = px(26.);

/// The project a session is in: its folder's or repo's last name ("Assembly" for `https://github.com/me/Assembly`).
pub fn project(repo_url: &str) -> &str {
    let url = repo_url.trim_end_matches(['/', '\\']);
    let url = url.strip_suffix(".git").unwrap_or(url);
    super::lightbox::last_part(url)
}

/// What the header calls the session's project.
fn project_name(item: &SessionItem) -> &str {
    if item.is_bare {
        NO_PROJECT
    } else {
        project(&item.repo_url)
    }
}

#[derive(Default)]
pub struct Header {
    /// The open session, once the topic has sent it.
    item: Option<SessionItem>,
    parent_key: Option<SessionKey>,
    /// Owns the keyed `sessions` subscription; dropping it unsubscribes.
    _feed: Option<Task<()>>,
}

impl Header {
    pub(super) fn set_parent(&mut self, parent: Option<SessionKey>, cx: &mut Context<Self>) {
        self.parent_key = parent;
        cx.notify();
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
    /// With no session open it still draws, holding only the sessions button, so a column left closed can reopen.
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let look = Look::of(cx);
        // The look's crumb: a 32px ghost button that shows and hides the sessions column, in text-3.
        let sessions = div().debug_selector(|| "toggle-sessions".into()).child(
            Button::new("toggle-sessions")
                .control(ids::SESSIONS_TOGGLE, "Show or hide the sessions column")
                .ghost()
                .size(BUTTON_HEIGHT)
                .px_0()
                .rounded(CORNER_PILL)
                .text_color(look.text_3)
                .tooltip("Sessions")
                .child(Icon::new(IconName::PanelLeft).size(CRUMB_ICON))
                .on_click(|_, window, cx| {
                    if let Some(dock) = CenterPanes::dock(cx) {
                        dock.update(cx, |dock, cx| {
                            dock.toggle_dock(DockPlacement::Left, window, cx)
                        });
                    }
                }),
        );
        // The crumb's line: 46 high under a hairline, its pieces 8 apart.
        let row = h_flex()
            .h(CRUMB_HEIGHT)
            .gap(SPACE[3])
            .pl(SPACE[3])
            .pr(SPACE[6])
            .border_b_1()
            .border_color(look.hairline)
            .text_role(TextRole::Body, cx)
            .text_color(look.text_2)
            .child(sessions);
        let Some(item) = &self.item else {
            return div().child(row);
        };
        let parent = self.parent_key.clone();
        div().child(
            // "project › Session title": the project in text-2, the chevron in text-3, the title in text-1 at 600.
            row.child(
                h_flex()
                    .flex_1()
                    .min_w_0()
                    .gap(SPACE[3])
                    .child(div().flex_none().child(project_name(item).to_string()))
                    .child(
                        Icon::new(IconName::ChevronRight)
                            .size(CRUMB_ICON)
                            .text_color(look.text_3),
                    )
                    .child(
                        div()
                            .min_w_0()
                            .truncate()
                            .text_role(TextRole::Title, cx)
                            .text_color(look.text_1)
                            .font_weight(FontWeight::SEMIBOLD)
                            .child(item.title()),
                    ),
            )
            // The provider at the right in a quiet pill (the kit's tag, its line and no fill), with the machine when
            // it isn't this one, then the link to the session that started it.
            .child(
                h_flex()
                    .flex_none()
                    .gap(SPACE[1])
                    .child(provider_pill(item, cx))
                    .when_some(parent, |line, parent| {
                        line.child("·").child(
                            Button::new("parent-session")
                                .control(ids::PARENT_SESSION, "Parent session")
                                .link()
                                .label("Parent session")
                                .on_click(move |_, window, cx| {
                                    window.dispatch_action(
                                        Box::new(SessionOpenRequest {
                                            key: parent.clone(),
                                            request_id: uuid::Uuid::new_v4().to_string(),
                                            target: SessionOpenTarget::Engine,
                                        }),
                                        cx,
                                    )
                                }),
                        )
                    }),
            ),
        )
    }
}

/// The look's quiet pill at the crumb's right (`.runpill`'s size): the kit's tag with the crumb's line and no fill,
/// naming the provider and any other machine in text-2. The provider's mark waits on the `sessions` item carrying the
/// glyph the rail's entries already have.
fn provider_pill(item: &SessionItem, cx: &App) -> Tag {
    let look = Look::of(cx);
    Tag::custom(transparent_black(), look.text_2, look.line)
        .rounded_full()
        .h(PILL_HEIGHT)
        .px(SPACE[5])
        .text_role(TextRole::Meta, cx)
        .child(item.place())
}

#[cfg(test)]
#[path = "tests/header_tests.rs"]
mod tests;

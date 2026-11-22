//! `Header` is the 46 px crumb above the timeline, as the look's `.crumb` draws it: a button that shows and hides the
//! sessions column, then "project › Session title", and at the right the provider (and any other machine) in a quiet
//! pill and, for a session another session started, a link to the session that started it (the open session intent,
//! lane g0-host). The center draws no tab row over a lone pane, so this button stands in for the one that row had.
//! Built from the kit's Button, Tag and Icon. It follows the `sessions` topic keyed on the open session. Replaces
//! SessionHeader.svelte.

use crate::components::quiet_button::quiet_icon_button;
use crate::core::clock::backend_clock::BackendClock;
use crate::core::clock::tick::Tick;
use crate::core::theme::look::Look;
use crate::core::theme::metrics::{CRUMB_HEIGHT, SPACE};
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::core::ui::access::Control as _;
use crate::core::ui::access::control_ids::chat as ids;
use crate::features::sessions::history::age::age;
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
    engine::{EngineChanged, EngineFeed, EngineScope, SessionKey},
    intents::session_open::{SessionOpenRequest, SessionOpenTarget},
};

/// The crumb's icons: the sessions button's and the chevron.
const CRUMB_ICON: Pixels = px(16.);
/// The waiting pill's question icon.
const WAITING_ICON: Pixels = px(14.);
/// The crumb pills' height, the look's `.runpill`.
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
    /// The open session's engine key, used to spot open owner engine/workflow needs with no chat ask row.
    session_key: Option<SessionKey>,
    parent_key: Option<SessionKey>,
    /// The backend clock's last tick, for the waiting pill's age.
    now: Option<i64>,
    /// Held only while the waiting pill is shown with a start timestamp, so nothing runs at rest.
    clock: Option<(Entity<BackendClock>, Subscription)>,
    /// Watches `EngineFeed` only while a session key is open.
    engine: Option<(Entity<EngineFeed>, Subscription)>,
    /// Owns the keyed `sessions` subscription; dropping it unsubscribes.
    _feed: Option<Task<()>>,
}

impl Header {
    pub(super) fn set_parent(&mut self, parent: Option<SessionKey>, cx: &mut Context<Self>) {
        self.parent_key = parent;
        cx.notify();
    }

    pub(super) fn set_session_key(&mut self, key: Option<SessionKey>, cx: &mut Context<Self>) {
        self.session_key = key;
        self.engine = self.session_key.as_ref().map(|_| {
            let feed = EngineFeed::global(cx);
            let heard = cx.subscribe(&feed, |header, _, event: &EngineChanged, cx| {
                if header.engine_relevant(event) {
                    header.sync_clock(cx);
                    cx.notify();
                }
            });
            (feed, heard)
        });
        self.sync_clock(cx);
        cx.notify();
    }

    /// Follows `session`, or shows nothing.
    pub fn show(&mut self, session: Option<String>, cx: &mut Context<Self>) {
        self.item = None;
        self.session_key = None;
        self.now = None;
        self.clock = None;
        self.engine = None;
        self._feed = None;
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
                    header.sync_clock(cx);
                    cx.notify();
                });
                if shown.is_err() {
                    return;
                }
            }
        }));
    }

    /// `Some(since)` while the session is waiting on the user (open chat ask or open owner engine need), anchoring
    /// `since` to the earliest still-open need; `None` while running, failed, or idle.
    fn waiting_state(&self, cx: &App) -> Option<Option<i64>> {
        let item = self.item.as_ref()?;
        let chat_waiting = item.state == "waiting";
        let chat_since = chat_waiting.then_some(item.state_since).flatten();
        let (engine_waiting, engine_since) = self.engine_waiting(cx);
        if !chat_waiting && !engine_waiting {
            return None;
        }
        let since = match (chat_since, engine_since) {
            (Some(chat), Some(engine)) => Some(chat.min(engine)),
            (chat, engine) => chat.or(engine),
        };
        Some(since)
    }

    fn engine_waiting(&self, cx: &App) -> (bool, Option<i64>) {
        let (Some(key), Some((feed, _))) = (&self.session_key, &self.engine) else {
            return (false, None);
        };
        let feed = feed.read(cx);
        let scope = EngineScope {
            machine: key.machine.clone(),
            topic: "needs_me".into(),
            key: None,
        };
        if !feed.loaded(&scope) {
            return (false, None);
        }
        let mut waiting = false;
        let mut earliest: Option<i64> = None;
        for need in feed.needs_for(key) {
            if need.state == "open" && need.for_owner {
                waiting = true;
                if let Some(created) = need.created_ms {
                    earliest = Some(earliest.map_or(created, |current| current.min(created)));
                }
            }
        }
        (waiting, earliest)
    }

    fn engine_relevant(&self, event: &EngineChanged) -> bool {
        let Some(key) = &self.session_key else {
            return false;
        };
        event.sessions.iter().any(|session| session == key)
            || event
                .scopes
                .iter()
                .any(|scope| scope.machine == key.machine && scope.topic == "needs_me")
            || event
                .keys
                .iter()
                .any(|item| item.machine == key.machine && item.kind == "needs_me")
    }

    /// Holds the backend clock only while the waiting pill is shown with a start timestamp.
    fn sync_clock(&mut self, cx: &mut Context<Self>) {
        let needs_clock =
            self.waiting_state(cx).flatten().is_some() && SocketClient::try_global(cx).is_some();
        if needs_clock {
            if self.clock.is_none() {
                let clock = cx.new(|cx| {
                    let mut clock = BackendClock::new();
                    clock.hold(cx);
                    clock
                });
                let ticks = cx.subscribe(&clock, |header, _, tick: &Tick, cx| {
                    header.tick(tick.now, cx)
                });
                self.clock = Some((clock, ticks));
            }
        } else {
            self.now = None;
            self.clock = None;
        }
    }

    /// The waiting age is minutes at the finest, so a tick redraws only when the minute turns.
    fn tick(&mut self, now: i64, cx: &mut Context<Self>) {
        let turned = self.now.map(|held| held / 60_000) != Some(now / 60_000);
        self.now = Some(now);
        if turned {
            cx.notify();
        }
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
            quiet_icon_button("toggle-sessions", IconName::PanelLeft, cx)
                .control(ids::SESSIONS_TOGGLE, "Show or hide the sessions column")
                .tooltip("Sessions")
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
        let waiting = self.waiting_state(cx);
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
            // Any waiting pill, then the provider in a quiet pill (with the machine when it isn't this one), then the
            // link to the session that started it.
            .child(
                h_flex()
                    .flex_none()
                    .gap(SPACE[2])
                    .when_some(waiting, |line, since| {
                        line.child(waiting_pill(since, self.now, cx))
                    })
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

/// The look's `.runpill`, shown only while the session is waiting on the user.
fn waiting_pill(since: Option<i64>, now: Option<i64>, cx: &App) -> Tag {
    let look = Look::of(cx);
    let label = match (since, now) {
        (Some(since), Some(now)) => format!("Waiting on you · {}", age(now, since)),
        _ => "Waiting on you".into(),
    };
    Tag::custom(look.card, look.text_2, look.line)
        .rounded_full()
        .h(PILL_HEIGHT)
        .pl(SPACE[2])
        .pr(SPACE[4])
        .text_role(TextRole::Meta, cx)
        .child(
            h_flex()
                .items_center()
                .gap(SPACE[2])
                .child(
                    Icon::new(IconName::CircleQuestionMark)
                        .size(WAITING_ICON)
                        .text_color(look.needs_you),
                )
                .child(label),
        )
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

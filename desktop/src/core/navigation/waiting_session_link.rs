//! A session link from when it arrives until its open is answered or its minute is up. Only one exists: a newer link
//! replaces it, and dropping it drops what it watches and its timer. The wait pushes off state changes.

use std::time::Duration;

use gpui_kit::*;

use super::deep_links::show_notice;
use crate::core::intents::open_session::OpenSession;
use crate::core::intents::session_open::{LINK_REQUEST, SessionOpenResult};
use crate::core::socket::client::{GlobalBulk, GlobalSocket, SocketClient};
use crate::core::state::machines::{GlobalMachines, Machines};
use crate::core::state::selected_session::SelectedSession;
use crate::core::state::sessions::{GlobalSessions, Sessions};
use crate::core::ui::center_panes::CenterPanes;
use crate::features::session::view::session_pane::SessionPane;
use crate::features::sessions::sessions_pane::{not_connected, refusal_words};
use crate::features::tasks::tasks_pane::NOT_OPENABLE;

const WAIT: Duration = Duration::from_secs(60);

struct Current {
    _link: Option<Entity<WaitingSessionLink>>,
}

impl Global for Current {}

/// Starts opening a session from a link, in place of any link still waiting.
pub fn open(id: String, machine: Option<String>, cx: &mut App) {
    cx.set_global(Current { _link: None });
    let link = cx.new(|cx| WaitingSessionLink::new(id, machine, cx));
    cx.set_global(Current {
        _link: Some(link.clone()),
    });
    // The newest open wins, so a link that has just arrived may replace one that's out.
    link.update(cx, |link, cx| {
        if link.ended_on_a_missing_machine(cx) {
            return;
        }
        if SessionPane::can_open_links(cx) {
            link.send_open(cx);
        }
    });
}

struct WaitingSessionLink {
    id: String,
    machine: Option<String>,
    /// The request of the open not answered yet.
    opening: Option<String>,
    /// Something changed since the last try, or there hasn't been one.
    try_owed: bool,
    /// The words of the last refusal.
    refusal: Option<String>,
    minute_up: bool,
    /// The sockets and lists as they are now; each may start after the link arrives.
    watching: Vec<Subscription>,
    _starts: [Subscription; 5],
    _answers: Subscription,
    _timer: Task<()>,
}

impl WaitingSessionLink {
    fn new(id: String, machine: Option<String>, cx: &mut Context<Self>) -> Self {
        let timer = cx.background_executor().timer(WAIT);
        let mut link = Self {
            id,
            machine,
            opening: None,
            try_owed: true,
            refusal: None,
            minute_up: false,
            watching: Vec::new(),
            _starts: [
                cx.observe_global::<GlobalSocket>(Self::started),
                cx.observe_global::<GlobalBulk>(Self::started),
                cx.observe_global::<GlobalMachines>(Self::started),
                cx.observe_global::<GlobalSessions>(Self::started),
                cx.observe_global::<CenterPanes>(Self::started),
            ],
            _answers: cx.subscribe(&SelectedSession::global(cx), |link, _, event, cx| {
                link.answered(event, cx)
            }),
            _timer: cx.spawn(async move |link, cx| {
                timer.await;
                _ = link.update(cx, |link, cx| link.time_up(cx));
            }),
        };
        link.watch_what_exists(cx);
        link
    }

    fn started(&mut self, cx: &mut Context<Self>) {
        self.watch_what_exists(cx);
        self.try_open(cx);
    }

    fn watch_what_exists(&mut self, cx: &mut Context<Self>) {
        let mut watching = Vec::new();
        for socket in [SocketClient::try_global(cx), SocketClient::try_bulk(cx)]
            .into_iter()
            .flatten()
        {
            watching.push(cx.observe(&socket, |link, _, cx| link.try_open(cx)));
        }
        if let Some(machines) = Machines::try_global(cx) {
            watching.push(cx.observe(&machines, |link, _, cx| link.list_changed(cx)));
        }
        if let Some(sessions) = Sessions::try_global(cx) {
            watching.push(cx.observe(&sessions, |link, _, cx| link.list_changed(cx)));
        }
        self.watching = watching;
    }

    fn list_changed(&mut self, cx: &mut Context<Self>) {
        self.try_owed = true;
        self.try_open(cx);
    }

    /// Never over an open that's out, which it would cancel: a change during one is tried after its answer.
    fn try_open(&mut self, cx: &mut Context<Self>) {
        if self.ended_on_a_missing_machine(cx) {
            return;
        }
        if self.try_owed
            && self.opening.is_none()
            && SessionPane::can_open_links(cx)
            && !SessionPane::open_is_out(cx)
        {
            self.send_open(cx);
        }
    }

    /// A machine the loaded list doesn't have isn't coming, so the link ends now rather than at its minute.
    fn ended_on_a_missing_machine(&self, cx: &mut Context<Self>) -> bool {
        let missing = Machines::missing(self.machine.as_deref(), cx);
        if let (true, Some(machine)) = (missing, &self.machine) {
            end(Some(not_connected(machine)), cx);
        }
        missing
    }

    fn send_open(&mut self, cx: &mut Context<Self>) {
        self.try_owed = false;
        let request_id = format!("{LINK_REQUEST}{}", uuid::Uuid::new_v4());
        self.opening = Some(request_id.clone());
        cx.dispatch_action(&OpenSession {
            id: self.id.clone(),
            route_machine: self.machine.clone(),
            request_id,
            historical: false,
        });
    }

    fn answered(&mut self, event: &SessionOpenResult, cx: &mut Context<Self>) {
        if self.opening.as_ref() != Some(&event.request_id) {
            // A person's open wins. Any other that was out has finished, so this link may try.
            return if event.is_from_a_person() {
                end(None, cx)
            } else {
                self.try_open(cx)
            };
        }
        self.opening = None;
        let refusal = match &event.result {
            Ok(()) => return end(None, cx),
            Err(refusal) if refusal.code == "cancelled" => return end(None, cx),
            Err(refusal) => refusal,
        };
        let words = refusal_words(self.machine.as_deref(), refusal);
        // A machine answers before its sessions can be opened.
        let machine_may_be_coming = refusal.code == "source_changed"
            && self.machine.is_some()
            && !Machines::offline(self.machine.as_deref(), cx);
        if self.minute_up || !machine_may_be_coming {
            return end(Some(words), cx);
        }
        self.refusal = Some(words);
        self.try_open(cx);
    }

    /// An open still out when the minute ends gets its answer: it may yet work.
    fn time_up(&mut self, cx: &mut Context<Self>) {
        self.minute_up = true;
        if self.opening.is_none() {
            let words = self.refusal.take();
            end(Some(words.unwrap_or_else(|| NOT_OPENABLE.into())), cx);
        }
    }
}

fn end(notice: Option<String>, cx: &mut App) {
    cx.set_global(Current { _link: None });
    if let Some(notice) = notice {
        show_notice(notice, cx);
    }
}

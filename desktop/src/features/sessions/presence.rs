//! `Presence` is what a session's row shows of what it's doing, from the summary the backend keeps on its item
//! (step 1c) and the machines list: its machine isn't answering, it needs you (an open question or permission), it's
//! working, its last turn failed, a turn ended that you haven't seen, or nothing. The first that holds wins, in that
//! order.

use gpui_kit::App;

use crate::core::state::machines::Machines;
use crate::core::state::session_item::SessionItem;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Presence {
    Offline,
    NeedsYou,
    Working,
    Failed,
    Unread,
    Idle,
}

impl Presence {
    /// `offline`: whether the session's machine didn't answer last time (`Machines::offline`).
    pub fn of(item: &SessionItem, offline: bool) -> Presence {
        if offline {
            Presence::Offline
        } else if item.state == "waiting" {
            Presence::NeedsYou
        } else if item.active_run_id.is_some() {
            Presence::Working
        } else if item.state == "failed" {
            Presence::Failed
        } else if item.unread {
            Presence::Unread
        } else {
            Presence::Idle
        }
    }

    /// The session's presence now, with its machine's state from the machines list.
    pub fn now(item: &SessionItem, cx: &App) -> Presence {
        Presence::of(item, Machines::offline(item.machine.as_deref(), cx))
    }

    /// The word the row adds after its agent and machine, for the ones that ask something of you or explain a gap.
    pub fn word(self) -> Option<&'static str> {
        match self {
            Presence::Offline => Some("offline"),
            Presence::NeedsYou => Some("needs you"),
            Presence::Failed => Some("failed"),
            _ => None,
        }
    }
}

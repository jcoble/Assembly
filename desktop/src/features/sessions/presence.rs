//! `Presence` is what a session's row shows of what it's doing, from the summary the backend keeps on its item
//! (step 1c): it needs you (an open question or permission), it's working, its last turn failed, a turn ended that
//! you haven't seen, or nothing. The first that holds wins, in that order.

use crate::core::state::session_item::SessionItem;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Presence {
    NeedsYou,
    Working,
    Failed,
    Unread,
    Idle,
}

impl Presence {
    pub fn of(item: &SessionItem) -> Presence {
        if item.state == "waiting" {
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

    /// The word the row adds after its agent and machine, for the two that ask something of you.
    pub fn word(self) -> Option<&'static str> {
        match self {
            Presence::NeedsYou => Some("needs you"),
            Presence::Failed => Some("failed"),
            _ => None,
        }
    }
}

//! `Presence` is what a rail row says its session is doing, from the backend's read (a parent's counts its helpers):
//! it needs you (an open question or permission), it's working, its last turn failed, the user marked it done, a
//! turn ended that you haven't seen, or nothing. The first that holds wins, in that order. A machine that isn't
//! answering doesn't change it: the row keeps the last state it knew and says it's offline beside it.

use super::rail_page::RailEntry;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Presence {
    NeedsYou,
    Working,
    Failed,
    Done,
    Unread,
    Idle,
}

impl Presence {
    pub fn of(entry: &RailEntry) -> Presence {
        if entry.needs_you {
            Presence::NeedsYou
        } else if entry.running {
            Presence::Working
        } else if entry.failed {
            Presence::Failed
        } else if entry.completed_at.is_some() {
            Presence::Done
        } else if entry.unread {
            Presence::Unread
        } else {
            Presence::Idle
        }
    }

    /// The word on the row's pill, for the states that have one.
    pub fn word(self) -> Option<&'static str> {
        match self {
            Presence::NeedsYou => Some("Needs you"),
            Presence::Working => Some("Working"),
            Presence::Failed => Some("Failed"),
            Presence::Done => Some("Done"),
            Presence::Unread | Presence::Idle => None,
        }
    }
}

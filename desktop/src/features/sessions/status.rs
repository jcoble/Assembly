//! `Status` is which of the user's three groups a session is in: Working, Done or Settled. It's read from the
//! session's `completedAt` and `settledAt` stamps, which only the user's own mark (`sessions/mark`) sets.

use crate::core::state::session_item::SessionItem;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Status {
    Working,
    Done,
    Settled,
}

impl Status {
    /// The groups in the order the list draws them.
    pub const ALL: [Status; 3] = [Status::Working, Status::Done, Status::Settled];

    pub fn of(item: &SessionItem) -> Status {
        match (item.completed_at, item.settled_at) {
            (_, Some(_)) => Status::Settled,
            (Some(_), None) => Status::Done,
            (None, None) => Status::Working,
        }
    }

    pub fn label(self) -> &'static str {
        match self {
            Status::Working => "Working",
            Status::Done => "Done",
            Status::Settled => "Settled",
        }
    }

    /// The `state` `sessions/mark` takes.
    pub fn wire(self) -> &'static str {
        match self {
            Status::Working => "working",
            Status::Done => "done",
            Status::Settled => "settled",
        }
    }

    /// Where the row's menu can move a session from here, as (menu label, new status).
    pub fn moves(self) -> [(&'static str, Status); 2] {
        match self {
            Status::Working => [("Mark done", Status::Done), ("Settle", Status::Settled)],
            Status::Done => [
                ("Settle", Status::Settled),
                ("Back to working", Status::Working),
            ],
            Status::Settled => [
                ("Unsettle", Status::Done),
                ("Back to working", Status::Working),
            ],
        }
    }
}

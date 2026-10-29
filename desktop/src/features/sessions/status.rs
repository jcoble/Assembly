//! `Status` is the user's own mark on a session: Working, Done or Settled, from its `completedAt` and `settledAt`
//! stamps, which only `sessions/mark` sets. The rail holds no settled session (History does), so a row is Done once
//! it has a completion stamp. A row's menu moves it from there.

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Status {
    Working,
    Done,
    Settled,
}

impl Status {
    /// A rail row's mark, from its completion stamp.
    pub fn of(completed_at: Option<i64>) -> Status {
        match completed_at {
            Some(_) => Status::Done,
            None => Status::Working,
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

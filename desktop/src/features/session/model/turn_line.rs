//! The line under a turn ("Worked for 42s · Thu 12:02 PM · model · 1.2k in · …", "Working for 9s", "Stopped",
//! "Failed") and the checkout line above a prompt when the run started on another branch or commit. Ported from
//! session/turns.ts.

use crate::features::session::rows::turn_stats::TurnStats;

/// The line under a turn. A live turn with no stats yet shows only its running time, from the backend clock's `now`
/// (core/clock).
pub fn turn_line(_stats: Option<&TurnStats>, _live_started_at: Option<i64>, _now: i64) -> String {
    todo!()
}

/// "Checkout changed · main@1a2b3c4 → feat@5d6e7f8", or `None` when the run started where the last one left off.
pub fn checkout_line(_stats: Option<&TurnStats>) -> Option<String> {
    todo!()
}

/// Assembly's "Worked for" duration: 9.5s, 42s, 3m 5s.
pub fn worked_for(_ms: i64) -> String {
    todo!()
}

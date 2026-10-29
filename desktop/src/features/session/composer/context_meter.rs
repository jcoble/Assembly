//! `ContextMeter` is how full the agent's context window is, for the composer's ring and its hint ("51,285 of
//! 1,000,000 (5%)"), from the newest `context` row. Not the window's performance meter (lane g0-host). Replaces
//! contextMeter.ts.

#[derive(Clone, Debug, PartialEq)]
pub struct ContextMeter {
    /// Shares of the window, rounded: what's left and what's spent.
    pub left: u8,
    pub used: u8,
    /// What the ring paints: what's left, but never more than 90 once anything is spent.
    pub arc: u8,
    pub warm: bool,
    pub hot: bool,
    pub detail: String,
}

impl ContextMeter {
    /// `None` when the agent hasn't reported, or reports more used than the window holds.
    pub fn of(used: i64, size: i64) -> Option<ContextMeter> {
        if size <= 0 || used < 0 || used > size {
            return None;
        }
        let spent = used as f64 / size as f64 * 100.0;
        let left = (100.0 - spent).round() as u8;
        Some(ContextMeter {
            left,
            used: 100 - left,
            arc: if spent <= 0.0 {
                100
            } else {
                (100.0 - spent.max(MIN_SPENT_ARC)).round() as u8
            },
            warm: left <= 25,
            hot: left <= 10,
            detail: format!(
                "{left}% context left · {} of {} ({}%)",
                thousands(used),
                thousands(size),
                100 - left
            ),
        })
    }
}

/// The smallest spent share the ring paints: a true 0.5% is a pixel of gap.
const MIN_SPENT_ARC: f64 = 10.0;

/// 1,000,000.
fn thousands(n: i64) -> String {
    let digits = n.to_string();
    let mut out = String::new();
    for (i, digit) in digits.chars().enumerate() {
        if i > 0 && (digits.len() - i).is_multiple_of(3) {
            out.push(',');
        }
        out.push(digit);
    }
    out
}

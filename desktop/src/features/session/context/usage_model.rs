//! `usage` turns the selected session's reported figures into what the Context usage card shows, ported from the old
//! app's `sessionContextUsage`. A figure nobody sent stays `None`, so the card says it wasn't reported rather than
//! showing a zero.

#[derive(Clone, Debug, PartialEq)]
pub struct Usage {
    pub used: Option<i64>,
    pub window: Option<i64>,
    /// Used of the window, rounded, at most 100; only when both are real and the window is above zero.
    pub percent: Option<u8>,
    pub input: Option<i64>,
    pub output: Option<i64>,
    /// The session's total.
    pub total: Option<i64>,
}

/// `total_read` is false until `rows/tokens` has answered, which holds the running-total rule.
///
/// The old rule: with no total reported, a used figure above the window while in plus out fit it is really a running
/// total, so in plus out becomes the current context and used becomes the session's total.
pub fn usage(
    used: Option<i64>,
    window: Option<i64>,
    input: Option<i64>,
    output: Option<i64>,
    total: Option<i64>,
    total_read: bool,
) -> Usage {
    let positive = |n: Option<i64>| n.filter(|n| *n > 0);
    let (window, input, output, total) = (
        positive(window),
        positive(input),
        positive(output),
        positive(total),
    );
    let mut used = used.filter(|n| *n >= 0);
    let mut total = total;
    if let (true, None, Some(reported), Some(size), Some(input), Some(output)) =
        (total_read, total, used, window, input, output)
        && reported > size
        && input + output <= size
    {
        (used, total) = (Some(input + output), Some(reported));
    }
    let percent = match (used, window) {
        (Some(used), Some(size)) => {
            Some((used as f64 / size as f64 * 100.).round().min(100.) as u8)
        }
        _ => None,
    };
    Usage {
        used,
        window,
        percent,
        input,
        output,
        total,
    }
}

#[cfg(test)]
#[path = "usage_model_tests.rs"]
mod tests;

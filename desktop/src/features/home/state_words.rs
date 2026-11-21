//! The words and colours the Home panel gives each state the backend pushes. Nothing is decided here: each state
//! arrives derived, and this only names it. A state this desktop doesn't know yet reads as nothing rather than a code.

use crate::activity::Tone;

/// A provider's row, or one machine's line inside it: Ready, Setting up, Needs sign-in, Problem. `None` is a
/// provider switched off.
pub fn provider_state(state: Option<&str>) -> Option<(&'static str, Tone)> {
    Some(match state {
        None => ("Off", Tone::Neutral),
        Some("ready") => ("Ready", Tone::Success),
        Some("setting_up") => ("Setting up", Tone::Info),
        Some("needs_sign_in") => ("Needs sign-in", Tone::Warning),
        Some("problem") => ("Problem", Tone::Danger),
        Some(_) => return None,
    })
}

/// A machine's connection.
pub fn connection(state: Option<&str>) -> Option<(&'static str, Tone)> {
    Some(match state? {
        "connected" => ("Connected", Tone::Success),
        "checking" => ("Checking", Tone::Neutral),
        "offline" => ("Offline", Tone::Danger),
        "disconnected" => ("Disconnected", Tone::Neutral),
        _ => return None,
    })
}

/// A machine's update, said only when there is something to say: up to date and unknown say nothing.
pub fn update(state: Option<&str>) -> Option<(&'static str, Tone)> {
    Some(match state? {
        "update_available" => ("Update available", Tone::Info),
        "updating" => ("Updating", Tone::Info),
        "waiting_on_sessions" => ("Waiting on sessions", Tone::Warning),
        "restart_required" => ("Restart to finish updating", Tone::Warning),
        "restarting" => ("Restarting", Tone::Info),
        "failed" => ("Update failed", Tone::Danger),
        "cant_update" => ("Can't update", Tone::Warning),
        _ => return None,
    })
}

/// The top line's colour: calm when nothing needs the user, amber when something does, red for a problem.
pub fn summary_tone(kind: &str) -> Tone {
    match kind {
        "nothing" => Tone::Success,
        "problem" => Tone::Danger,
        _ => Tone::Warning,
    }
}

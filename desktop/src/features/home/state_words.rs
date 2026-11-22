//! The words and colours the Home panel gives each state the backend pushes. Nothing is decided here: each state
//! arrives derived, and this only names it. A state this desktop doesn't know yet reads as nothing rather than a code.

use crate::activity::Tone;

/// A provider's row: its state, or Off when the backend sends none because it is switched off.
pub fn provider_state(state: Option<&str>) -> Option<(&'static str, Tone)> {
    match state {
        None => Some(("Off", Tone::Neutral)),
        Some(state) => row_state(state),
    }
}

/// One machine's line inside a provider: Ready, Setting up, Needs sign-in, Problem. A machine that is offline or
/// not checked yet has no state, and its line says nothing; the machine's own row says why.
pub fn machine_state(state: Option<&str>) -> Option<(&'static str, Tone)> {
    row_state(state?)
}

fn row_state(state: &str) -> Option<(&'static str, Tone)> {
    Some(match state {
        "ready" => ("Ready", Tone::Success),
        "setting_up" => ("Setting up", Tone::Info),
        "needs_sign_in" => ("Needs sign-in", Tone::Warning),
        "problem" => ("Problem", Tone::Danger),
        _ => return None,
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
        "setting_up" => Tone::Info,
        "problem" => Tone::Danger,
        _ => Tone::Warning,
    }
}

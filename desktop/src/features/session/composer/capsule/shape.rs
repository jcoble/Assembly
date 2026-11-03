//! What shape the box takes and what the one button beside the draft does, from the old composer's rules. Empty, the
//! box is a single slim line when the pane is wide enough. Content, a turn's first five seconds, a pending answer or a
//! drag opens it, and it stays open while a message is being written. Focus never opens it: focus arrives from too many
//! places to predict.

use gpui_kit::{Pixels, px};

use crate::features::session::model::item::{Item, ItemKind};

/// Narrower than this the controls would squeeze the placeholder away, so the box keeps two rows at rest.
pub const ONE_LINE_FROM: Pixels = px(760.);
/// At this width or less the settings pills collapse into the compact menu.
pub const COMPACT_TO: Pixels = px(440.);

#[derive(Clone, Copy, Debug, Default)]
pub struct Facts {
    pub content: bool,
    pub sending: bool,
    /// The turn has run five seconds, so an empty box goes back to its line and the reply has the room.
    pub settled: bool,
    pub locked: bool,
    pub dragging: bool,
    /// Content opened the box, and nothing has been sent since.
    pub opened: bool,
    /// The pane's width; zero until it has been measured.
    pub width: Pixels,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Look {
    pub relaxed: bool,
    pub one_line: bool,
    pub compact: bool,
}

pub fn look(facts: Facts) -> Look {
    let opening =
        facts.content || (facts.sending && !facts.settled) || facts.locked || facts.dragging;
    let relaxed = opening || facts.opened;
    Look {
        relaxed,
        one_line: !relaxed && facts.width >= ONE_LINE_FROM,
        compact: facts.width > px(0.) && facts.width <= COMPACT_TO,
    }
}

/// Why the draft can't be written in now: the agent is waiting on an answer elsewhere.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum Lock {
    Approval,
    Input,
}

/// The draft's hint: what's waiting, or who the message goes to.
pub fn hint(provider: Option<&str>, lock: Option<Lock>) -> String {
    match (lock, provider) {
        (Some(Lock::Approval), _) => "Resolve the approval above to continue".into(),
        (Some(Lock::Input), _) => "Complete the requested input above".into(),
        (None, Some(provider)) => format!("Message {provider}"),
        (None, None) => "Message".into(),
    }
}

/// The one slot beside the draft.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum Slot {
    Send { steer: bool },
    Stop,
}

/// Stop while a turn runs and there's nothing that can join it: an empty draft, or an agent that can't take a steer.
pub fn slot(running: bool, content: bool, can_steer: bool) -> Slot {
    if running && (!content || !can_steer) {
        Slot::Stop
    } else {
        Slot::Send { steer: running }
    }
}

#[cfg(test)]
#[path = "shape_tests.rs"]
mod tests;

/// What the agent is waiting on, which holds the draft until it has an answer.
pub fn pending_answer(items: &[Item]) -> Option<Lock> {
    items.iter().rev().find_map(|item| match &item.kind {
        ItemKind::Ask { ask, .. } if ask.state == "open" => Some(if ask.kind == "permission" {
            Lock::Approval
        } else {
            Lock::Input
        }),
        _ => None,
    })
}

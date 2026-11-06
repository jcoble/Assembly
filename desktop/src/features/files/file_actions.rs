//! One native action attempt. An unconfirmed filesystem result is refreshed, never replayed.
use serde::Deserialize;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum ActionState {
    #[default]
    Idle,
    Running,
    Applied,
    Failed,
    Unknown,
}
#[derive(Default)]
pub struct ActionRun {
    pub state: ActionState,
    pub error: Option<String>,
}
#[derive(Deserialize)]
pub struct ActionReply {
    pub state: String,
    pub path: Option<String>,
    pub error: Option<ActionFault>,
}
#[derive(Deserialize)]
pub struct ActionFault {
    pub message: String,
}
impl ActionRun {
    pub fn begin(&mut self) -> bool {
        if !matches!(self.state, ActionState::Idle | ActionState::Failed) {
            return false;
        }
        self.state = ActionState::Running;
        self.error = None;
        true
    }
    pub fn unknown(&mut self) {
        self.state = ActionState::Unknown;
        self.error = Some("The action result is unknown. The tree was refreshed; inspect both paths before another action.".into());
    }
    pub fn complete(&mut self, expected: &str, reply: ActionReply) -> bool {
        if self.state != ActionState::Running {
            return false;
        }
        if reply.state == "applied" && reply.path.as_deref() == Some(expected) {
            self.state = ActionState::Applied;
            self.error = None;
            return true;
        }
        if reply.state == "failed" {
            self.state = ActionState::Failed;
            self.error = Some(reply.error.map_or_else(
                || "The file action was refused.".into(),
                |fault| fault.message,
            ));
        } else {
            self.unknown();
        }
        false
    }
}
pub fn named_target(folder: &str, name: &str) -> Result<String, &'static str> {
    if name.is_empty() || matches!(name, "." | "..") || name.contains('/') || name.contains('\0') {
        return Err("Enter one file or folder name, without path separators.");
    }
    let path = if folder.is_empty() {
        name.to_string()
    } else {
        format!("{folder}/{name}")
    };
    if path.len() > 4096 {
        return Err("The relative path is longer than 4096 bytes.");
    }
    Ok(path)
}
#[cfg(test)]
#[path = "file_actions_tests.rs"]
mod tests;

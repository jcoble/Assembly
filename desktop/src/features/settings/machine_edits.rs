//! The Connections section's edits as data: the `machines/set` and `machines/remove` arguments from what was typed,
//! and what a call's answer leaves on the page. Nothing here changes the machines list: only the `machines` topic
//! does, so an edit shows as saved only once the backend took it.

use serde_json::{Value, json};

use crate::core::socket::outcome::Outcome;
use crate::core::state::machine::Machine;

#[derive(Default)]
pub(super) struct MachineEdits {
    /// The saved machine the form edits, as it was when the edit began; `None` adds one.
    pub editing: Option<Machine>,
    /// The last call that failed, in plain words, with the machine whose row made it (`None` for the form).
    pub failure: Option<(Option<String>, String)>,
}

impl MachineEdits {
    /// Takes a call's answer, from the row of machine `about` or from the form (`None`), and says whether the backend
    /// took it. A failure is kept to show where the call was made and leaves the rest as it was, so the form keeps
    /// what was typed; the form's save, once taken, ends its edit.
    pub fn settle<T>(&mut self, about: Option<String>, doing: &str, outcome: Outcome<T>) -> bool {
        let why = match outcome {
            Outcome::Ok(_) => {
                if about.is_none() {
                    self.editing = None;
                }
                return true;
            }
            Outcome::Failed(failure) => failure.message,
            Outcome::Aborted => "the connection to the backend closed".into(),
        };
        self.failure = Some((about, format!("{doing}: {}.", why.trim_end_matches('.'))));
        false
    }

    /// Follows the machines list while no save of the form's is on its way: an edit whose machine is gone, or was
    /// reached another way (its SSH destination, port or token file changed), ends and says why, so its Save can't
    /// bring the machine back or undo that change. Whether a machine answers doesn't touch the edit. Says whether it
    /// ended.
    pub fn follow<'a>(&mut self, machines: impl IntoIterator<Item = &'a Machine>) -> bool {
        let Some(edited) = &self.editing else {
            return false;
        };
        let name = edited.name.clone().unwrap_or_default();
        let why = match machines.into_iter().find(|held| held.name == edited.name) {
            None => format!("Edit ended: {name} was removed, so nothing was saved."),
            Some(held)
                if (&held.ssh, held.port, &held.token_file)
                    != (&edited.ssh, edited.port, &edited.token_file) =>
            {
                format!(
                    "Edit ended: {name} was changed elsewhere, so nothing was saved. Edit it again to see how it's reached now."
                )
            }
            Some(_) => return false,
        };
        self.editing = None;
        self.failure = Some((None, why));
        true
    }
}

/// `machines/set`'s arguments. A blank port or token file is left out, so the backend's default applies.
pub(super) fn set_args(
    name: &str,
    ssh: &str,
    port: &str,
    token_file: &str,
) -> Result<Value, String> {
    let mut args = json!({ "name": name_of(name)?, "ssh": ssh.trim() });
    let port = port.trim();
    if !port.is_empty() {
        match port.parse::<u16>() {
            Ok(port) if port > 0 => args["port"] = json!(port),
            _ => return Err("The port is a number from 1 to 65535.".into()),
        }
    }
    let token_file = token_file.trim();
    if !token_file.is_empty() {
        args["tokenFile"] = json!(token_file);
    }
    Ok(args)
}

/// `machines/remove`'s arguments.
pub(super) fn remove_args(name: &str) -> Result<Value, String> {
    Ok(json!({ "name": name_of(name)? }))
}

/// The machine's name, its key: not empty, and not `local`, this Mac's key.
fn name_of(name: &str) -> Result<&str, String> {
    match name.trim() {
        "" => Err("Name the machine.".into()),
        "local" => Err("“local” is this Mac's name; choose another.".into()),
        name => Ok(name),
    }
}

#[cfg(test)]
#[path = "machine_edits_tests.rs"]
mod tests;

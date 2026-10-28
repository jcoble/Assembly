//! `Machine` is one machine from the `machines` topic (dotnet/Assembly.Application/Common/Machines/MachineStatus.cs,
//! `MachineItem`): whether it answered last time, and why not.

use serde::Deserialize;

#[derive(Clone, Debug, Deserialize)]
pub struct Machine {
    /// `None` for this machine.
    pub name: Option<String>,
    pub kind: String,
    pub reachable: bool,
    /// Why it didn't answer, when it didn't.
    pub reason: Option<String>,
}

/// The reason a remote has before its first probe (MachineStatus.cs).
const NOT_CHECKED: &str = "not checked yet";

impl Machine {
    /// A probe found it down; one not probed yet hasn't failed to answer.
    pub fn didnt_answer(&self) -> bool {
        !self.reachable && self.reason.as_deref() != Some(NOT_CHECKED)
    }
}

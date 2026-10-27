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

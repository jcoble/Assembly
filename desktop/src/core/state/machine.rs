//! `Machine` is one machine from the `machines` topic (dotnet/Assembly.Application/Common/Machines/MachineStatus.cs,
//! `MachineItem`): whether it answered last time, and why not, and for a saved remote how it's reached.

use serde::Deserialize;

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Machine {
    /// `None` for this machine.
    pub name: Option<String>,
    pub kind: String,
    pub reachable: bool,
    /// Why it didn't answer, when it didn't.
    pub reason: Option<String>,
    /// False for a remote until its first probe answers or fails: until then it's neither up nor down.
    #[serde(default)]
    pub checked: bool,
    /// A remote's SSH destination, port and token file (a path on the remote); `None` for this machine.
    #[serde(default)]
    pub ssh: Option<String>,
    #[serde(default)]
    pub port: Option<u16>,
    #[serde(default)]
    pub token_file: Option<String>,
}

impl Machine {
    /// A probe found it down; one not probed yet hasn't failed to answer.
    pub fn didnt_answer(&self) -> bool {
        self.checked && !self.reachable
    }
}

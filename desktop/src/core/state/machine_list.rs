//! `MachineList` is `machines/list`'s answer.

use serde::Deserialize;

use super::machine::Machine;

#[derive(Deserialize)]
pub struct MachineList {
    pub machines: Vec<Machine>,
}

//! `Machines` owns the machines list, read with `machines/list` (the remote machines; their reachability comes with
//! the base lane's machines step M1). It reads again on each reconnect.

use gpui_kit::*;

use super::machine::Machine;

pub struct Machines {
    machines: Vec<Machine>,
    loading: Option<Task<()>>,
}

impl Machines {
    /// Reads the list.
    pub fn new(_cx: &mut Context<Self>) -> Self {
        todo!()
    }

    pub fn machines(&self) -> &[Machine] {
        &self.machines
    }

    fn load(&mut self, _cx: &mut Context<Self>) {
        todo!()
    }
}

//! `Machines` owns the machines list, read with `machines/list`: each machine, and whether it answered last time. It
//! reads the list again on each reconnect, until the backend's machines topic replaces the call.

use gpui_kit::*;
use serde_json::json;

use super::machine::Machine;
use super::machine_list::MachineList;
use crate::core::socket::client::SocketClient;
use crate::core::socket::connection_state::ConnectionState;
use crate::core::socket::outcome::Outcome;

pub struct Machines {
    machines: Vec<Machine>,
    loading: Option<Task<()>>,
    _reconnects: Subscription,
}

impl Machines {
    /// Reads the list.
    pub fn new(cx: &mut Context<Self>) -> Self {
        let client = SocketClient::global(cx);
        let reconnects = cx.subscribe(&client, |machines, _, state, cx| {
            if matches!(state, ConnectionState::Connected { .. }) {
                machines.load(cx);
            }
        });
        let mut machines = Self {
            machines: Vec::new(),
            loading: None,
            _reconnects: reconnects,
        };
        machines.load(cx);
        machines
    }

    pub fn machines(&self) -> &[Machine] {
        &self.machines
    }

    fn load(&mut self, cx: &mut Context<Self>) {
        let list = SocketClient::global(cx).read(cx).call::<MachineList>(
            "machines",
            "list",
            json!({}),
            Some("machines/list"),
        );
        self.loading = Some(cx.spawn(async move |this, cx| {
            let outcome = list.await;
            let _ = this.update(cx, |machines, cx| {
                machines.loading = None;
                if let Outcome::Ok(list) = outcome {
                    machines.machines = list.machines;
                    cx.notify();
                }
            });
        }));
    }
}

//! `Machines` owns the machines list, from the `machines` topic: each machine, whether it answered last time, and why
//! not. The topic's state comes at subscribe and again after each reconnect, then each change as an upsert; the
//! backend probes the remote machines while it has a subscriber.

use gpui_kit::*;
use serde_json::Value;

use super::machine::Machine;
use crate::core::socket::client::SocketClient;
use crate::core::socket::update::Update;

const TOPIC: &str = "machines";

pub struct Machines {
    /// In the order they first came, by the topic's key: the name, or "local" for this machine.
    machines: Vec<(String, Machine)>,
    /// Owns the `machines` subscription; dropping it unsubscribes.
    _feed: Task<()>,
}

impl Machines {
    /// Subscribes to the `machines` topic.
    pub fn new(cx: &mut Context<Self>) -> Self {
        let mut subscription = SocketClient::global(cx).read(cx).subscribe(TOPIC, None);
        let feed = cx.spawn(async move |this, cx| {
            while let Ok(update) = subscription.updates.recv().await {
                if this
                    .update(cx, |machines, cx| machines.apply(update, cx))
                    .is_err()
                {
                    return;
                }
            }
        });
        Self {
            machines: Vec::new(),
            _feed: feed,
        }
    }

    pub fn machines(&self) -> impl Iterator<Item = &Machine> {
        self.machines.iter().map(|(_, machine)| machine)
    }

    fn apply(&mut self, update: Update, cx: &mut Context<Self>) {
        match update {
            Update::State(items) => items
                .into_iter()
                .for_each(|(key, item)| read(&mut self.machines, key, item)),
            Update::Change { key, item } => read(&mut self.machines, key, item),
        }
        cx.notify();
    }
}

fn read(machines: &mut Vec<(String, Machine)>, key: String, item: Value) {
    match serde_json::from_value(item) {
        Ok(machine) => upsert(machines, key, machine),
        Err(error) => eprintln!("machines: an item that doesn't read: {error}"),
    }
}

/// Replaces the machine with `key` in place, or adds it at the end.
fn upsert(machines: &mut Vec<(String, Machine)>, key: String, machine: Machine) {
    match machines.iter_mut().find(|(held, _)| *held == key) {
        Some((_, held)) => *held = machine,
        None => machines.push((key, machine)),
    }
}

#[cfg(test)]
mod tests {
    use serde_json::json;

    use super::{Machine, upsert};

    fn machine(name: Option<&str>, reachable: bool) -> Machine {
        serde_json::from_value(
            json!({"name": name, "kind": "remote", "reachable": reachable, "reason": null}),
        )
        .expect("a machine")
    }

    // Pins: g0-socket#14
    #[test]
    fn a_change_replaces_its_machine_in_place() {
        let mut machines = vec![];
        upsert(&mut machines, "local".into(), machine(None, true));
        upsert(&mut machines, "box".into(), machine(Some("box"), false));
        upsert(&mut machines, "box".into(), machine(Some("box"), true));
        let keys: Vec<&str> = machines.iter().map(|(key, _)| key.as_str()).collect();
        assert_eq!(keys, ["local", "box"]);
        assert!(machines[1].1.reachable);
    }
}

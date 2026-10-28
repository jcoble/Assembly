//! `Machines` owns the machines list, from the `machines` topic: each machine, whether it answered last time, and why
//! not. The topic's state, at subscribe and again after each reconnect, is the whole list and replaces it; each change
//! after it is an upsert. The backend probes the remote machines while it has a subscriber.

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

/// Set once the socket client has started; observing it tells a view the list is there.
pub(crate) struct GlobalMachines(Entity<Machines>);

impl Global for GlobalMachines {}

impl Machines {
    /// Starts the window's machines list, once the socket client has started.
    pub fn start(cx: &mut App) {
        let state = cx.new(Machines::new);
        cx.set_global(GlobalMachines(state));
    }

    /// The machines list, once the socket client has started.
    pub fn try_global(cx: &App) -> Option<Entity<Self>> {
        cx.try_global::<GlobalMachines>()
            .map(|state| state.0.clone())
    }

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

    /// Whether the machine `name` didn't answer last time. This machine (`None`), one the list doesn't have yet and one
    /// not probed yet aren't offline.
    pub fn offline(name: Option<&str>, cx: &App) -> bool {
        let Some(name) = name else {
            return false;
        };
        Machines::try_global(cx).is_some_and(|machines| {
            (machines.read(cx).machines())
                .any(|held| held.name.as_deref() == Some(name) && held.didnt_answer())
        })
    }

    fn apply(&mut self, update: Update, cx: &mut Context<Self>) {
        match update {
            Update::State(items) => replace(&mut self.machines, items),
            Update::Change { key, item } => read(&mut self.machines, key, item),
        }
        cx.notify();
    }
}

/// The topic's state is the whole list, in its order.
fn replace(machines: &mut Vec<(String, Machine)>, items: Vec<(String, Value)>) {
    machines.clear();
    items
        .into_iter()
        .for_each(|(key, item)| read(machines, key, item));
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

    use super::{Machine, replace, upsert};

    fn machine(name: Option<&str>, reachable: bool) -> Machine {
        serde_json::from_value(
            json!({"name": name, "kind": "remote", "reachable": reachable, "reason": null, "checked": true}),
        )
        .expect("a machine")
    }

    // A remote starts "not checked yet": up or down isn't known, so it isn't offline until a probe fails.
    // Pins: sessions-list#11
    #[test]
    fn only_a_probe_that_failed_makes_a_machine_offline() {
        let mut waiting = machine(Some("box"), false);
        waiting.checked = false;
        assert!(!waiting.didnt_answer());
        assert!(machine(Some("box"), false).didnt_answer());
        assert!(!machine(Some("box"), true).didnt_answer());
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

    // The state is the whole list: a machine the backend no longer has is gone after it (a reconnect's state).
    // Pins: g0-socket#14
    #[test]
    fn a_state_replaces_the_list() {
        let mut machines = vec![
            ("local".into(), machine(None, true)),
            ("box".into(), machine(Some("box"), true)),
        ];
        let local = json!({"name": null, "kind": "local", "reachable": true, "reason": null});
        replace(&mut machines, vec![("local".into(), local)]);
        let keys: Vec<&str> = machines.iter().map(|(key, _)| key.as_str()).collect();
        assert_eq!(keys, ["local"]);
    }
}

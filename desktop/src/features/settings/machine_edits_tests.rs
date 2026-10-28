use serde_json::json;

use super::{MachineEdits, remove_args, set_args};
use crate::core::socket::call_failure::CallFailure;
use crate::core::socket::error_code::ErrorCode;
use crate::core::socket::outcome::Outcome;
use crate::core::state::machine::Machine;

fn saved(name: &str, ssh: &str, reachable: bool) -> Machine {
    serde_json::from_value(
        json!({"name": name, "kind": "remote", "reachable": reachable, "reason": null,
        "checked": true, "ssh": ssh, "port": 9797, "tokenFile": ".config/assembly-dotnet/env"}),
    )
    .expect("a machine")
}

fn editing(name: &str) -> MachineEdits {
    MachineEdits {
        editing: Some(saved(name, "host", true)),
        failure: None,
    }
}

fn edited(edits: &MachineEdits) -> Option<&str> {
    edits
        .editing
        .as_ref()
        .and_then(|machine| machine.name.as_deref())
}

fn failed(message: &str) -> Outcome<()> {
    Outcome::Failed(CallFailure {
        code: ErrorCode::Failed,
        message: message.into(),
    })
}

// What was typed goes to machines/set as it is, trimmed, with the port as a number.
#[test]
fn the_set_arguments_carry_what_was_typed() {
    let args = set_args(
        " Workbox Test ",
        " workbox-test ",
        " 9867 ",
        " .config/assembly-dotnet-settings2/env ",
    );
    assert_eq!(
        args,
        Ok(
            json!({"name": "Workbox Test", "ssh": "workbox-test", "port": 9867,
            "tokenFile": ".config/assembly-dotnet-settings2/env"})
        )
    );
}

// A blank port or token file is left out, so the backend fills in its defaults (9797 and .config/assembly-dotnet/env).
#[test]
fn a_blank_port_and_token_file_take_the_backends_defaults() {
    let args = set_args("box", "user@host", "  ", "");
    assert_eq!(args, Ok(json!({"name": "box", "ssh": "user@host"})));
}

// The name is the machine's key: none, or "local" (this Mac's key), is refused before anything is sent.
#[test]
fn an_empty_name_or_local_is_refused() {
    for name in ["", "   ", "local", " local "] {
        assert!(set_args(name, "host", "", "").is_err(), "set {name:?}");
        assert!(remove_args(name).is_err(), "remove {name:?}");
    }
    assert_eq!(remove_args(" box "), Ok(json!({"name": "box"})));
}

#[test]
fn a_port_that_isnt_one_is_refused() {
    for port in ["abc", "0", "70000", "-1", "9797x"] {
        assert!(set_args("box", "host", port, "").is_err(), "port {port:?}");
    }
}

// A failed save keeps the form on its edit, with what was typed, and says why under the form.
#[test]
fn a_failed_call_changes_nothing_and_records_why() {
    let mut edits = editing("box");
    let taken = edits.settle(
        None,
        "Not saved",
        failed("expected a non-local name, SSH destination, port and token file"),
    );
    assert!(!taken);
    assert_eq!(edited(&edits), Some("box"));
    assert_eq!(
        edits.failure,
        Some((
            None,
            "Not saved: expected a non-local name, SSH destination, port and token file.".into()
        ))
    );
}

// A row's call that never got an answer says so on that machine's row.
#[test]
fn a_dropped_call_shows_on_its_machine() {
    let mut edits = MachineEdits::default();
    let taken = edits.settle(
        Some("box".into()),
        "Couldn't disconnect",
        Outcome::<()>::Aborted,
    );
    assert!(!taken);
    assert_eq!(
        edits.failure,
        Some((
            Some("box".into()),
            "Couldn't disconnect: the connection to the backend closed.".into()
        ))
    );
}

// The form's save, once taken, ends the edit; a row's call that's taken leaves the form alone.
#[test]
fn a_taken_save_ends_the_edit() {
    let mut edits = editing("box");
    assert!(edits.settle(Some("box".into()), "Couldn't connect", Outcome::Ok(())));
    assert_eq!(edited(&edits), Some("box"));
    assert!(edits.settle(None, "Not saved", Outcome::Ok(())));
    assert_eq!(edited(&edits), None);
    assert_eq!(edits.failure, None);
}

// Removed elsewhere (another window, an agent): the edit ends and says so, so its Save can't bring the machine back.
#[test]
fn an_edit_ends_when_its_machine_is_removed() {
    let mut edits = editing("box");
    let other = saved("other", "other-host", true);
    assert!(edits.follow([&other]));
    assert_eq!(edited(&edits), None);
    assert_eq!(
        edits.failure,
        Some((
            None,
            "Edit ended: box was removed, so nothing was saved.".into()
        ))
    );
}

// A reconnect's state is the whole list: one without the machine ends its edit too.
#[test]
fn an_edit_ends_when_a_new_state_leaves_its_machine_out() {
    let mut edits = editing("box");
    let local: Machine = serde_json::from_value(
        json!({"name": null, "kind": "local", "reachable": true, "reason": null, "checked": true}),
    )
    .expect("this Mac");
    assert!(edits.follow([&local, &saved("other", "host", true)]));
    assert_eq!(edited(&edits), None);
    assert!(edits.failure.is_some());
}

// Changed elsewhere: Save would put the old SSH destination, port or token file back, so the edit ends.
#[test]
fn an_edit_ends_when_its_machine_is_reached_another_way() {
    for change in [
        json!({"ssh": "new-host"}),
        json!({"port": 9867}),
        json!({"tokenFile": "other/env"}),
    ] {
        let mut changed = json!({"name": "box", "kind": "remote", "reachable": true, "checked": true, "ssh": "host",
            "port": 9797, "tokenFile": ".config/assembly-dotnet/env"});
        changed
            .as_object_mut()
            .unwrap()
            .extend(change.as_object().unwrap().clone());
        let changed: Machine = serde_json::from_value(changed).unwrap();
        let mut edits = editing("box");
        assert!(edits.follow([&changed]), "{change}");
        assert_eq!(edited(&edits), None);
        assert_eq!(
            edits.failure,
            Some((None, "Edit ended: box was changed elsewhere, so nothing was saved. Edit it again to see how it's reached now.".into()))
        );
    }
}

// The probes flip whether a machine answers all the time; that leaves the edit, and what was typed, alone.
#[test]
fn an_answer_or_a_failed_probe_keeps_the_edit() {
    let mut edits = editing("box");
    assert!(!edits.follow([&saved("box", "host", false)]));
    assert!(!edits.follow([&saved("box", "host", true)]));
    assert_eq!(edited(&edits), Some("box"));
    assert_eq!(edits.failure, None);
}

use super::{Machine, behind_notice, behind_words, item_words};
use crate::core::state::machine::UpdateItem;
use crate::features::settings::connections::row_status;
use serde_json::json;

fn machine(value: serde_json::Value) -> Machine {
    serde_json::from_value(value).unwrap()
}

fn with_updates() -> Machine {
    machine(
        json!({"name":"box","kind":"remote","reachable":true,"reason":null,"checked":true,
        "backend":{"commit":"a1b2c3d4e5","localCommit":"f6e5d4c3b2","differs":true,"version":"0.1.0","localVersion":"0.2.0","versionDiffers":false},
        "updates":[
            {"kind":"backend","name":"Backend","current":"0.1.0","wanted":"0.1.0","state":"behind"},
            {"kind":"adapter","name":"codex-acp","current":"1.0.0","wanted":"1.1.0","state":"behind"},
            {"kind":"cli","name":"codex","current":"2.0.0","wanted":"2.0.0","state":"current"},
            {"kind":"cli","name":"claude","current":null,"wanted":null,"state":"unknown"}]}),
    )
}

#[test]
fn it_reads_the_exact_wire_shape_and_older_backends_without_any_of_it() {
    assert_eq!(with_updates().behind().count(), 2);
    let plain =
        machine(json!({"name":null,"kind":"local","reachable":true,"reason":null,"checked":true}));
    assert_eq!(plain.behind().count(), 0);
    assert_eq!(plain.backend_note(), None);
}

#[test]
fn the_notice_names_what_is_behind_and_stays_quiet_otherwise() {
    let machines = [with_updates()];
    assert_eq!(
        behind_words(machines.iter()).as_deref(),
        Some("Behind on box: Backend, codex-acp.")
    );
    let current = [machine(
        json!({"name":null,"kind":"local","reachable":true,"reason":null,"checked":true,
        "updates":[{"kind":"cli","name":"codex","current":"2.0.0","wanted":"2.0.0","state":"current"}]}),
    )];
    assert_eq!(behind_words(current.iter()), None);
}

#[test]
fn what_is_ahead_says_so_and_a_backend_says_this_app_is_lower() {
    let item = |kind: &str, name: &str, current: &str, wanted: &str| UpdateItem {
        kind: kind.into(),
        name: name.into(),
        current: Some(current.into()),
        wanted: Some(wanted.into()),
        state: "newer".into(),
    };
    assert_eq!(
        item_words(&item("backend", "Backend", "0.2.0", "0.1.0")),
        "Backend 0.2.0, newer than this app's 0.1.0"
    );
    assert_eq!(
        item_words(&item("adapter", "codex-acp", "2.0.0", "1.0.0")),
        "codex-acp 2.0.0, newer than 1.0.0"
    );
}

#[test]
fn the_notice_points_at_the_button_a_delivered_app_has() {
    let text = behind_notice("Behind on box: codex-acp.");
    assert_eq!(
        text,
        "Behind on box: codex-acp. Use Update everything in Settings to update them."
    );
    assert!(!text.contains(crate::features::remote_install::TITLE));
}

#[test]
fn each_piece_says_where_it_is_and_where_it_should_be() {
    let items = with_updates().updates;
    assert_eq!(item_words(&items[1]), "codex-acp 1.0.0 → 1.1.0");
    assert_eq!(item_words(&items[2]), "codex 2.0.0");
    assert_eq!(item_words(&items[3]), "claude");
}

#[test]
fn the_row_shows_the_commit_and_what_is_behind_as_information() {
    let shown = with_updates();
    assert_eq!(
        row_status(&shown, "host", "Connected"),
        "host · Connected · Backend a1b2c3d, differs from this app's f6e5d4c · 2 behind"
    );
    let same = machine(
        json!({"name":"box","kind":"remote","reachable":true,"reason":null,"checked":true,
        "backend":{"commit":"a1b2c3d4e5","localCommit":"a1b2c3d4e5","differs":false}}),
    );
    assert_eq!(
        row_status(&same, "host", "Connected"),
        "host · Connected · Backend a1b2c3d, same as this app"
    );
    let updating = machine(
        json!({"name":"box","kind":"remote","reachable":true,"reason":null,"checked":true,
        "updating":true,"updateNote":"Waiting."}),
    );
    assert_eq!(
        row_status(&updating, "host", "Connected"),
        "host · Connected · Waiting."
    );
}

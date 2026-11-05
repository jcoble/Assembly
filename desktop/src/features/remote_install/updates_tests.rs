use super::{
    Machine, behind_notice, behind_words, newer_words, notice_words, row_status, update_offered,
};
use crate::core::socket::client::SocketClient;
use crate::core::socket::endpoint::Endpoint;
use crate::core::socket::fake_backend::{FakeBackend, Reply, hello};
use crate::core::socket::test_wait::until;
use crate::core::state::machine::UpdateItem;
use crate::core::state::machines::Machines;
use futures_channel::mpsc::unbounded;
use gpui_kit::component::WindowExt as _;
use gpui_kit::{AppContext as _, EmptyView};
use serde_json::{Value, json};

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
        newer_words(&item("backend", "Backend", "0.2.0", "0.1.0")).as_deref(),
        Some("Backend 0.2.0, newer than this app's 0.1.0")
    );
    assert_eq!(
        newer_words(&item("adapter", "codex-acp", "2.0.0", "1.0.0")).as_deref(),
        Some("codex-acp 2.0.0, newer than 1.0.0")
    );
}

#[test]
fn the_notice_points_at_the_button_a_delivered_app_has() {
    let text = behind_notice("Behind on box: codex-acp.");
    assert_eq!(
        text,
        "Behind on box: codex-acp. Use Update everything in Settings to update them."
    );
}

#[test]
fn a_check_says_nothing_is_behind_only_when_every_piece_was_read() {
    let checked = |updates: serde_json::Value| {
        machine(
            json!({"name":"box","kind":"remote","reachable":true,"reason":null,"checked":true,"updates":updates}),
        )
    };
    let current =
        json!({"kind":"cli","name":"codex","current":"2.0.0","wanted":"2.0.0","state":"current"});
    let unread =
        json!({"kind":"cli","name":"claude","current":null,"wanted":null,"state":"unknown"});
    assert_eq!(
        row_status(&checked(json!([current])), "host", "Connected"),
        "host · Connected · Nothing is behind"
    );
    assert_eq!(
        row_status(&checked(json!([current, unread])), "host", "Connected"),
        "host · Connected · Couldn't read claude"
    );
    assert_eq!(
        row_status(&checked(json!([])), "host", "Connected"),
        "host · Connected"
    );
}

#[test]
fn the_row_shows_the_commit_and_what_is_behind_as_information() {
    let shown = with_updates();
    assert_eq!(
        row_status(&shown, "host", "Connected"),
        "host · Connected · Backend a1b2c3d, differs from this app's f6e5d4c · Backend, codex-acp behind"
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

// With nothing there to ask, "same as this app" and "Nothing is behind" were only what it last said; a real card said
// them after the install had been removed.
#[test]
fn a_machine_that_did_not_answer_claims_no_versions_but_keeps_its_note() {
    let offline = |extra: serde_json::Value| {
        let mut value = json!({"name":"box","kind":"remote","reachable":false,"reason":"didn't answer","checked":true,
            "backend":{"commit":"a1b2c3d4e5","localCommit":"a1b2c3d4e5","differs":false},
            "updates":[{"kind":"adapter","name":"codex-acp","current":"1.0.0","wanted":"1.1.0","state":"behind"}]});
        value
            .as_object_mut()
            .unwrap()
            .extend(extra.as_object().unwrap().clone());
        machine(value)
    };
    assert_eq!(
        row_status(&offline(json!({})), "host", "Offline"),
        "host · Offline"
    );
    assert_eq!(
        row_status(
            &offline(json!({"updateNote":"Install failed: no."})),
            "host",
            "Offline"
        ),
        "host · Offline · Install failed: no."
    );
    assert_eq!(
        row_status(
            &offline(json!({"updating":true,"updateNote":"Sending…"})),
            "host",
            "Offline"
        ),
        "host · Offline · Sending…"
    );
}

#[test]
fn a_ready_update_says_so_and_stops_counting_as_behind_on_this_machine() {
    let ready = machine(
        json!({"name":null,"kind":"local","reachable":true,"reason":null,"checked":true,"restartReady":"2.0.0",
        "updates":[{"kind":"desktop","name":"Desktop","current":"1.0.0","wanted":"2.0.0","state":"behind"}]}),
    );
    assert_eq!(
        row_status(&ready, "Local", "Connected"),
        "Local · Connected · Assembly 2.0.0 installs when you quit, or now with Restart to Update"
    );
    assert_eq!(behind_words(std::iter::once(&ready)), None);
}

#[test]
fn with_an_update_ready_this_machines_tools_can_still_be_updated_from_its_card() {
    let tools = machine(
        json!({"name":null,"kind":"local","reachable":true,"reason":null,"checked":true,"restartReady":"2.0.0",
        "updates":[{"kind":"desktop","name":"Desktop","current":"1.0.0","wanted":"2.0.0","state":"behind"},
                   {"kind":"cli","name":"codex","current":"1.0.0","wanted":"2.0.0","state":"behind"}]}),
    );
    assert!(update_offered(&tools));
    assert_eq!(
        row_status(&tools, "Local", "Connected"),
        "Local · Connected · Assembly 2.0.0 installs when you quit, or now with Restart to Update · codex behind"
    );
    assert_eq!(
        behind_words(std::iter::once(&tools)).as_deref(),
        Some("Behind on This machine: codex.")
    );

    // Only the app itself behind, and ready: that's the restart button's.
    let app = machine(
        json!({"name":null,"kind":"local","reachable":true,"reason":null,"checked":true,"restartReady":"2.0.0",
        "updates":[{"kind":"desktop","name":"Desktop","current":"1.0.0","wanted":"2.0.0","state":"behind"}]}),
    );
    assert!(!update_offered(&app));
}

#[test]
fn a_press_of_restart_shows_on_the_card_as_well() {
    let waiting = machine(
        json!({"name":null,"kind":"local","reachable":true,"reason":null,"checked":true,"restartReady":"2.0.0",
        "restarting":true,"restartNote":"Waiting for running sessions to finish…"}),
    );
    assert_eq!(
        row_status(&waiting, "Local", "Connected"),
        "Local · Connected · Waiting for running sessions to finish… · Assembly 2.0.0 installs when you quit, or now with Restart to Update"
    );
}

#[test]
fn a_machine_this_app_cant_update_says_why_and_offers_neither_button() {
    let old = machine(
        json!({"name":"box","kind":"remote","reachable":false,"reason":"not checked yet","checked":true,
        "cantUpdate":"Saved with the token file x, not an install's own, so this app can't install or update here. Clear the token file in Settings to install.",
        "updates":[{"kind":"backend","name":"Backend","current":"0.1.0","wanted":null,"state":"unknown"}]}),
    );
    assert!(row_status(&old, "box", "Offline").contains("Saved with the token file x"));
    assert!(!update_offered(&old));
    assert!(!crate::features::settings::connections::install_offered(
        &old
    ));
}

#[test]
fn while_an_update_is_ready_the_notice_says_how_it_installs_and_nothing_about_being_behind() {
    let ready = machine(
        json!({"name":null,"kind":"local","reachable":true,"reason":null,"checked":true,"restartReady":"2.0.0",
        "updates":[{"kind":"desktop","name":"Desktop","current":"1.0.0","wanted":"2.0.0","state":"behind"},
                   {"kind":"cli","name":"codex","current":"1.0.0","wanted":"2.0.0","state":"behind"}]}),
    );
    let machines = [ready, with_updates()];
    assert_eq!(
        notice_words(machines.iter()).as_deref(),
        Some("Assembly 2.0.0 is ready. It installs when you quit, or now with Restart to Update.")
    );
}

#[test]
fn with_nothing_ready_the_notice_still_points_at_update_everything() {
    let machines = [with_updates()];
    assert_eq!(
        notice_words(machines.iter()).as_deref(),
        Some(
            "Behind on box: Backend, codex-acp. Use Update everything in Settings to update them."
        )
    );
}

// The update being ready is said over the "Desktop behind" toast that came before it, not beside it.
#[gpui::test]
async fn the_ready_notice_replaces_the_behind_notice(cx: &mut gpui::TestAppContext) {
    cx.executor().allow_parking();
    let mut sub = Value::Null;
    let backend = FakeBackend::start(move |frame| {
        if let Some(replies) = hello(frame) {
            return replies;
        }
        let id = frame["id"].clone();
        let local = |ready: Value| {
            json!({"name":null,"kind":"local","reachable":true,"reason":null,"checked":true,"restartReady":ready,
            "updates":[{"kind":"desktop","name":"Desktop","current":"1.0.0","wanted":"2.0.0","state":"behind"}]})
        };
        match (frame["t"].as_str(), frame["method"].as_str()) {
            (Some("sub"), _) => {
                sub = id.clone();
                vec![Reply::Frame(json!({"t":"state","sub":id,"items":[
                    {"key":"local","item":local(Value::Null)}]}))]
            }
            (Some("call"), Some("ready")) => vec![
                Reply::Frame(
                    json!({"t":"change","sub":sub,"key":"local","item":local(json!("2.0.0"))}),
                ),
                Reply::Frame(json!({"t":"result","id":id,"result":{}})),
            ],
            _ => Vec::new(),
        }
    });
    let (_, cx) = cx.add_window_view(|window, cx| {
        gpui_kit::init(cx);
        SocketClient::start(
            Endpoint {
                port: backend.port,
                token: "secret".into(),
            },
            cx,
        );
        Machines::start(cx);
        super::tell_when_behind(cx);
        let page = cx.new(|_| EmptyView);
        gpui_kit::base::Root::new(page, window, cx)
    });
    let (changed, mut changes) = unbounded();
    let machines = cx.update(|_, cx| Machines::try_global(cx).expect("the machines list"));
    let tell = changed.clone();
    let _heard = cx.update(|_, cx| cx.observe(&machines, move |_, _| _ = tell.unbounded_send(())));
    until("the behind notice", &mut changes, &changed, || {
        cx.update(|window, cx| window.notifications(cx).len()) == 1
    })
    .await;
    let call = cx.update(|_, cx| {
        SocketClient::global(cx)
            .read(cx)
            .call::<Value>("updates", "ready", json!({}), None)
    });
    _ = call.await;
    until("the update ready", &mut changes, &changed, || {
        cx.update(|_, cx| {
            let machines = machines.read(cx);
            machines
                .machines()
                .any(|machine| machine.restart_ready.is_some())
        })
    })
    .await;
    cx.run_until_parked();
    cx.update(|window, cx| assert_eq!(window.notifications(cx).len(), 1));
}

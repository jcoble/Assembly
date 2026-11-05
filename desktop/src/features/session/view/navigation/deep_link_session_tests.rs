//! Spec section 2: a session link opens the session, waits until it can, and says why when it can't.

use std::cell::RefCell;
use std::rc::Rc;
use std::time::Duration;

use gpui::TestAppContext;
use gpui_kit::{App, AppContext as _, Entity, Global, VisualTestContext};
use serde_json::json;

use super::test_navigation::{NavigationFixture, NavigationHost};
use crate::core::engine::EngineRefusal;
use crate::core::intents::open_session::OpenSession;
use crate::core::intents::session_open::SessionOpenResult;
use crate::core::navigation::deep_links::{handle_route, notices};
use crate::core::navigation::external_urls::ExternalRoute;
use crate::core::socket::client::SocketClient;
use crate::core::socket::endpoint::Endpoint;
use crate::core::socket::fake_backend::{FakeBackend, Reply};
use crate::core::state::machines::Machines;
use crate::core::state::selected_session::SelectedSession;
use crate::core::state::sessions::Sessions;

type Opens = Rc<RefCell<Vec<OpenSession>>>;

const NOT_CONNECTED: &str = "That session is on Box, which this app isn't connected to.";

struct Recorder(Opens);

impl Global for Recorder {}

/// The last action listener registered is the only one that runs, so this goes on again after anything that registers
/// the real one.
fn record_opens(cx: &mut App) {
    let heard = cx.global::<Recorder>().0.clone();
    cx.on_action(move |intent: &OpenSession, _| heard.borrow_mut().push(intent.clone()));
}

/// A window that can show notices, with `OpenSession` recorded instead of carried out.
fn window(cx: &mut TestAppContext) -> (Entity<NavigationHost>, Opens, &mut VisualTestContext) {
    cx.executor().allow_parking();
    let opens = Opens::default();
    let heard = opens.clone();
    cx.update(|cx| {
        gpui_kit::init(cx);
        SelectedSession::init(cx);
        cx.set_global(Recorder(heard));
        record_opens(cx);
    });
    let host = Rc::new(RefCell::new(None));
    let made = host.clone();
    let (_, cx) = cx.add_window_view(move |window, cx| {
        let content = cx.new(|_| NavigationHost::default());
        *made.borrow_mut() = Some(content.clone());
        gpui_kit::base::Root::new(content, window, cx)
    });
    cx.update(|window, _| window.activate_window());
    cx.run_until_parked();
    let host = host.borrow_mut().take().expect("the host");
    (host, opens, cx)
}

/// The app becomes ready: Chat exists and both sockets are up.
async fn ready(host: &Entity<NavigationHost>, cx: &mut VisualTestContext) -> NavigationFixture {
    let mut nav = cx.update(|window, cx| {
        let nav = NavigationFixture::new(false, window, cx);
        record_opens(cx);
        nav
    });
    host.update(cx, |host, cx| {
        host.pane = Some(nav.pane.clone());
        cx.notify();
    });
    nav.ready(cx).await;
    cx.run_until_parked();
    nav
}

fn link(id: &str, machine: Option<&str>, cx: &mut VisualTestContext) {
    let route = ExternalRoute::Session {
        id: id.into(),
        machine: machine.map(String::from),
    };
    // As the queue does: outside any window update.
    cx.update(|_, cx| cx.defer(move |cx| handle_route(route, cx)));
    cx.run_until_parked();
}

fn answer(open: &OpenSession, result: Result<(), EngineRefusal>, cx: &mut VisualTestContext) {
    let event = SessionOpenResult {
        request_id: open.request_id.clone(),
        key: None,
        result,
    };
    cx.update(|_, cx| SelectedSession::global(cx).update(cx, |_, cx| cx.emit(event)));
    cx.run_until_parked();
}

fn refused(code: &str) -> Result<(), EngineRefusal> {
    Err(EngineRefusal {
        code: code.into(),
        message: "The reason.".into(),
    })
}

fn last(opens: &Opens) -> OpenSession {
    opens.borrow().last().expect("an open").clone()
}

fn shown(cx: &mut VisualTestContext) -> Vec<String> {
    cx.update(|_, cx| notices(cx))
}

fn hold_machines(reachable: bool, checked: bool, cx: &mut VisualTestContext) {
    cx.update(|_, cx| Machines::start_holding(box_machine(reachable, checked), cx));
    cx.run_until_parked();
}

fn box_machine(reachable: bool, checked: bool) -> Vec<(String, serde_json::Value)> {
    vec![(
        "Box".into(),
        json!({"name": "Box", "kind": "remote", "reachable": reachable, "checked": checked}),
    )]
}

fn advance(seconds: u64, cx: &mut VisualTestContext) {
    cx.executor().advance_clock(Duration::from_secs(seconds));
    cx.run_until_parked();
}

#[gpui::test]
async fn a_link_opens_its_session_in_chat(cx: &mut TestAppContext) {
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        SelectedSession::init(cx);
        crate::features::session::view::session_pane::SessionPane::register(cx);
    });
    let (host, cx) = cx.add_window_view(|_, _| NavigationHost::default());
    let mut nav = cx.update(|window, cx| NavigationFixture::new(false, window, cx));
    host.update(cx, |host, cx| {
        host.pane = Some(nav.pane.clone());
        cx.notify();
    });
    nav.ready(cx).await;
    link("s1", None, cx);
    nav.opened("s1", cx).await;
    cx.update(|_, cx| {
        assert_eq!(
            SelectedSession::global(cx).read(cx).session_id(),
            Some("s1")
        )
    });
}

#[gpui::test]
async fn a_link_sends_its_session_and_machine_with_a_fresh_request_each_time(
    cx: &mut TestAppContext,
) {
    let (host, opens, cx) = window(cx);
    let _nav = ready(&host, cx).await;
    hold_machines(true, true, cx);
    link("s1", None, cx);
    link("s2", Some("Box"), cx);
    let opens = opens.borrow();
    assert_eq!(opens.len(), 2);
    assert_eq!(opens[0].id, "s1");
    assert_eq!(opens[0].route_machine, None);
    assert!(!opens[0].historical);
    assert_eq!(opens[1].id, "s2");
    assert_eq!(opens[1].route_machine.as_deref(), Some("Box"));
    assert!(!opens[1].request_id.is_empty());
    assert_ne!(opens[0].request_id, opens[1].request_id);
}

#[gpui::test]
async fn a_link_before_the_connection_waits_for_it_quietly(cx: &mut TestAppContext) {
    let (host, opens, cx) = window(cx);
    link("s1", None, cx);
    assert!(opens.borrow().is_empty());
    assert_eq!(shown(cx), Vec::<String>::new());
    let _nav = ready(&host, cx).await;
    assert_eq!(opens.borrow().len(), 1);
    assert_eq!(last(&opens).id, "s1");
    assert_eq!(shown(cx), Vec::<String>::new());
}

#[gpui::test]
async fn a_connected_app_without_chat_still_waits(cx: &mut TestAppContext) {
    let (host, opens, cx) = window(cx);
    let backend = FakeBackend::start(|frame| match frame["t"].as_str() {
        Some("hello") => vec![Reply::Frame(
            json!({"t": "welcome", "protocol": 1, "machine": "box", "engine": "owner"}),
        )],
        _ => vec![],
    });
    let endpoint = Endpoint {
        port: backend.port,
        token: "secret".into(),
    };
    let main = cx.update(|_, cx| {
        let main = SocketClient::start(endpoint.clone(), cx);
        SocketClient::start_bulk(endpoint, cx);
        main
    });
    while cx.read(|cx| main.read(cx).connection_generation().is_none()) {
        cx.executor().timer(Duration::from_millis(5)).await;
        cx.executor().advance_clock(Duration::from_millis(5));
        cx.run_until_parked();
    }
    link("s1", None, cx);
    assert!(opens.borrow().is_empty());
    assert_eq!(shown(cx), Vec::<String>::new());
    let _nav = ready(&host, cx).await;
    assert_eq!(opens.borrow().len(), 1);
    assert_eq!(last(&opens).id, "s1");
}

#[gpui::test]
async fn a_link_during_a_later_reconnect_waits_too(cx: &mut TestAppContext) {
    let (host, opens, cx) = window(cx);
    let mut nav = ready(&host, cx).await;
    nav.drop_main(cx).await;
    link("s1", None, cx);
    nav.ready(cx).await;
    cx.run_until_parked();
    assert_eq!(opens.borrow().len(), 1);
    assert_eq!(last(&opens).id, "s1");
    assert_eq!(shown(cx), Vec::<String>::new());
}

#[gpui::test]
async fn a_machine_not_ready_yet_keeps_the_link_waiting_and_a_machine_change_tries_again(
    cx: &mut TestAppContext,
) {
    let (host, opens, cx) = window(cx);
    let _nav = ready(&host, cx).await;
    hold_machines(true, true, cx);
    link("s1", Some("Box"), cx);
    let first = last(&opens);
    answer(&first, refused("source_changed"), cx);
    assert_eq!(shown(cx), Vec::<String>::new(), "still waiting");
    assert_eq!(opens.borrow().len(), 1, "no retry until something changes");
    cx.update(|_, cx| Machines::change_holding(box_machine(true, true), cx));
    cx.run_until_parked();
    assert_eq!(opens.borrow().len(), 2);
    let second = last(&opens);
    assert_eq!(second.id, "s1");
    assert_eq!(second.route_machine.as_deref(), Some("Box"));
    assert_ne!(second.request_id, first.request_id);
    answer(&second, Ok(()), cx);
    assert_eq!(shown(cx), Vec::<String>::new());
}

#[gpui::test]
async fn a_session_list_change_also_tries_again(cx: &mut TestAppContext) {
    let (host, opens, cx) = window(cx);
    let _nav = ready(&host, cx).await;
    hold_machines(true, false, cx);
    cx.update(|_, cx| Sessions::start_holding(Vec::new(), false, false, cx));
    link("s1", Some("Box"), cx);
    answer(&last(&opens), refused("source_changed"), cx);
    assert_eq!(opens.borrow().len(), 1);
    cx.update(|_, cx| Sessions::change_holding(Vec::new(), cx));
    cx.run_until_parked();
    assert_eq!(opens.borrow().len(), 2);
}

#[gpui::test]
async fn a_machine_that_has_failed_gets_the_refusal_at_once(cx: &mut TestAppContext) {
    let (host, opens, cx) = window(cx);
    let _nav = ready(&host, cx).await;
    hold_machines(false, true, cx);
    link("s1", Some("Box"), cx);
    // It may try or not; if it tries, the backend refuses it.
    let tried = opens.borrow().last().cloned();
    if let Some(open) = tried {
        answer(&open, refused("source_changed"), cx);
    }
    assert_eq!(shown(cx), [NOT_CONNECTED]);
    let before = opens.borrow().len();
    cx.update(|_, cx| Machines::change_holding(box_machine(true, true), cx));
    cx.run_until_parked();
    assert_eq!(opens.borrow().len(), before, "the link is over");
}

#[gpui::test]
async fn a_machine_failing_during_the_wait_ends_it_with_the_refusal(cx: &mut TestAppContext) {
    let (host, opens, cx) = window(cx);
    let _nav = ready(&host, cx).await;
    hold_machines(true, true, cx);
    link("s1", Some("Box"), cx);
    answer(&last(&opens), refused("source_changed"), cx);
    cx.update(|_, cx| Machines::change_holding(box_machine(false, true), cx));
    cx.run_until_parked();
    let tried = opens.borrow().len();
    if tried == 2 {
        answer(&last(&opens), refused("source_changed"), cx);
    }
    assert_eq!(shown(cx), [NOT_CONNECTED]);
}

#[gpui::test]
async fn a_newer_link_replaces_one_waiting_for_the_connection(cx: &mut TestAppContext) {
    let (host, opens, cx) = window(cx);
    link("old", None, cx);
    link("new", None, cx);
    let _nav = ready(&host, cx).await;
    assert_eq!(opens.borrow().len(), 1);
    assert_eq!(last(&opens).id, "new");
}

#[gpui::test]
async fn a_newer_link_replaces_one_waiting_for_its_machine(cx: &mut TestAppContext) {
    let (host, opens, cx) = window(cx);
    let _nav = ready(&host, cx).await;
    hold_machines(true, true, cx);
    link("old", Some("Box"), cx);
    let old = last(&opens);
    answer(&old, refused("source_changed"), cx);
    link("new", None, cx);
    assert_eq!(last(&opens).id, "new");
    let count = opens.borrow().len();
    cx.update(|_, cx| Machines::change_holding(box_machine(true, true), cx));
    cx.run_until_parked();
    assert_eq!(opens.borrow().len(), count, "the old link is gone");
    answer(&old, refused("nonresumable"), cx);
    assert_eq!(
        shown(cx),
        Vec::<String>::new(),
        "its answer means nothing now"
    );
}

#[gpui::test]
async fn a_refused_open_gives_the_rails_words(cx: &mut TestAppContext) {
    let (host, opens, cx) = window(cx);
    let _nav = ready(&host, cx).await;
    hold_machines(true, true, cx);
    link("s1", None, cx);
    answer(&last(&opens), refused("source_changed"), cx);
    link("s2", None, cx);
    answer(&last(&opens), refused("unknown_session"), cx);
    link("s3", Some("Box"), cx);
    answer(&last(&opens), refused("nonresumable"), cx);
    assert_eq!(
        shown(cx),
        [
            "That session didn't open: The reason.",
            "That session didn't open: The reason.",
            "That session didn't open: The reason.",
        ]
    );
    assert_eq!(opens.borrow().len(), 3, "none of them waits");
}

#[gpui::test]
async fn a_superseded_open_says_nothing(cx: &mut TestAppContext) {
    let (host, opens, cx) = window(cx);
    let _nav = ready(&host, cx).await;
    link("s1", None, cx);
    answer(&last(&opens), refused("cancelled"), cx);
    assert_eq!(shown(cx), Vec::<String>::new());
}

#[gpui::test]
async fn an_open_that_worked_ends_the_link(cx: &mut TestAppContext) {
    let (host, opens, cx) = window(cx);
    let _nav = ready(&host, cx).await;
    hold_machines(true, true, cx);
    link("s1", Some("Box"), cx);
    answer(&last(&opens), Ok(()), cx);
    cx.update(|_, cx| Machines::change_holding(box_machine(true, true), cx));
    cx.run_until_parked();
    assert_eq!(opens.borrow().len(), 1);
    advance(61, cx);
    assert_eq!(shown(cx), Vec::<String>::new());
}

#[gpui::test]
async fn a_link_that_never_got_to_try_gives_up_after_a_minute(cx: &mut TestAppContext) {
    let (host, opens, cx) = window(cx);
    link("s1", None, cx);
    advance(59, cx);
    assert_eq!(shown(cx), Vec::<String>::new());
    advance(2, cx);
    assert_eq!(shown(cx), ["Assembly can't open that link."]);
    let _nav = ready(&host, cx).await;
    assert!(opens.borrow().is_empty(), "the link was dropped");
}

#[gpui::test]
async fn a_link_still_refused_after_a_minute_gives_the_last_refusal(cx: &mut TestAppContext) {
    let (host, opens, cx) = window(cx);
    let _nav = ready(&host, cx).await;
    hold_machines(true, true, cx);
    link("s1", Some("Box"), cx);
    answer(&last(&opens), refused("source_changed"), cx);
    advance(59, cx);
    assert_eq!(shown(cx), Vec::<String>::new());
    advance(2, cx);
    assert_eq!(shown(cx), [NOT_CONNECTED]);
    let count = opens.borrow().len();
    cx.update(|_, cx| Machines::change_holding(box_machine(true, true), cx));
    cx.run_until_parked();
    assert_eq!(opens.borrow().len(), count, "the link was dropped");
}

#[gpui::test]
async fn a_change_during_an_open_is_tried_after_its_answer(cx: &mut TestAppContext) {
    let (host, opens, cx) = window(cx);
    let _nav = ready(&host, cx).await;
    hold_machines(true, true, cx);
    link("s1", Some("Box"), cx);
    cx.update(|_, cx| Machines::change_holding(box_machine(true, true), cx));
    cx.run_until_parked();
    assert_eq!(opens.borrow().len(), 1, "one open at a time");
    answer(&last(&opens), refused("source_changed"), cx);
    assert_eq!(opens.borrow().len(), 2);
    assert_eq!(shown(cx), Vec::<String>::new());
}

#[gpui::test]
async fn an_open_still_out_when_the_minute_ends_gets_its_answer(cx: &mut TestAppContext) {
    let (host, opens, cx) = window(cx);
    let _nav = ready(&host, cx).await;
    hold_machines(true, true, cx);
    link("s1", Some("Box"), cx);
    advance(61, cx);
    assert_eq!(shown(cx), Vec::<String>::new());
    answer(&last(&opens), refused("source_changed"), cx);
    assert_eq!(shown(cx), [NOT_CONNECTED], "no more waiting");
}

// Spec section 2 point 8: the person's own open wins.
#[gpui::test]
async fn a_person_opening_a_session_ends_a_waiting_link(cx: &mut TestAppContext) {
    let (host, opens, cx) = window(cx);
    let _nav = ready(&host, cx).await;
    hold_machines(true, true, cx);
    link("s1", Some("Box"), cx);
    answer(&last(&opens), refused("source_changed"), cx);
    let person = OpenSession {
        id: "clicked".into(),
        route_machine: None,
        request_id: "person".into(),
        historical: false,
    };
    answer(&person, Ok(()), cx);
    cx.update(|_, cx| Machines::change_holding(box_machine(true, true), cx));
    cx.run_until_parked();
    assert_eq!(opens.borrow().len(), 1, "the link is over");
    advance(61, cx);
    assert_eq!(shown(cx), Vec::<String>::new());
}

#[gpui::test]
async fn a_link_does_not_retry_over_an_open_in_flight(cx: &mut TestAppContext) {
    use crate::core::engine::SessionKey;
    use crate::core::intents::session_open::{SessionOpenRequest, SessionOpenTarget};
    let (host, opens, cx) = window(cx);
    let nav = ready(&host, cx).await;
    hold_machines(true, true, cx);
    link("s1", Some("Box"), cx);
    answer(&last(&opens), refused("source_changed"), cx);
    nav.hold_target();
    nav.pane.update_in(cx, |pane, window, cx| {
        pane.request_open(
            SessionOpenRequest {
                key: SessionKey {
                    machine: "owner".into(),
                    id: "clicked".into(),
                },
                request_id: "person".into(),
                target: SessionOpenTarget::Historical,
            },
            window,
            cx,
        )
    });
    cx.run_until_parked();
    cx.update(|_, cx| Machines::change_holding(box_machine(true, true), cx));
    cx.run_until_parked();
    assert_eq!(opens.borrow().len(), 1, "the click isn't cut off");
}

#[gpui::test]
async fn only_a_person_opening_a_session_ends_a_waiting_link(cx: &mut TestAppContext) {
    let (host, opens, cx) = window(cx);
    let _nav = ready(&host, cx).await;
    hold_machines(true, true, cx);
    link("s1", Some("Box"), cx);
    answer(&last(&opens), refused("source_changed"), cx);
    let other = |request_id: &str| OpenSession {
        id: "other".into(),
        route_machine: None,
        request_id: request_id.into(),
        historical: false,
    };
    answer(&other("reconnect:1"), Ok(()), cx);
    answer(&other("person"), refused("cancelled"), cx);
    cx.update(|_, cx| Machines::change_holding(box_machine(true, true), cx));
    cx.run_until_parked();
    assert_eq!(opens.borrow().len(), 2, "the link still waits");
}

// Follow-up, finding 2: a machine missing from the loaded list can't come, so the link says so at once.
#[gpui::test]
async fn a_link_to_a_machine_missing_from_the_loaded_list_fails_at_once(cx: &mut TestAppContext) {
    let (host, opens, cx) = window(cx);
    let _nav = ready(&host, cx).await;
    hold_machines(true, true, cx);
    link("s2", Some("Nowhere"), cx);
    cx.run_until_parked();
    assert!(
        opens.borrow().is_empty(),
        "nothing to open on a machine the app doesn't have"
    );
    assert_eq!(
        shown(cx),
        ["That session is on Nowhere, which this app isn't connected to."]
    );
}

#[gpui::test]
async fn a_link_waiting_for_the_list_fails_once_the_list_lacks_its_machine(
    cx: &mut TestAppContext,
) {
    let (host, _opens, cx) = window(cx);
    let _nav = ready(&host, cx).await;
    link("s2", Some("Nowhere"), cx);
    assert!(shown(cx).is_empty(), "the list isn't here yet");
    hold_machines(true, true, cx);
    assert_eq!(
        shown(cx),
        ["That session is on Nowhere, which this app isn't connected to."]
    );
}

//! Feature 28 parity item 3: a Notion page link from anywhere opens its task in the Tasks tab when Notion is connected,
//! and in the browser when it isn't or when the page isn't a task in the connected source.

use std::sync::{Arc, Mutex};

use gpui::TestAppContext;
use gpui_kit::{Entity, VisualTestContext};
use serde_json::{Value, json};

use gpui_kit::AppContext as _;
use gpui_kit::component::dock::{DockArea, DockPlacement, panel_handle};

use super::{OpenPane, TasksPane};
use crate::core::intents::open_url::OpenUrl;
use crate::core::socket::client::SocketClient;
use crate::core::socket::endpoint::Endpoint;
use crate::core::socket::fake_backend::{FakeBackend, Reply, hello};
use crate::core::ui::center_panes::CenterPanes;
use crate::features::tasks::task_export::*;

const HEX: &str = "11111111111111111111111111111111";
const ID: &str = "11111111-1111-1111-1111-111111111111";

fn settings(connected: bool) -> NotionSettingsView {
    NotionSettingsView {
        data_source_id: if connected { "s".into() } else { String::new() },
        has_token: connected,
        generation: 1,
        state: "ready".into(),
        error: None,
    }
}

/// The window's Tasks tab, which links reach, and the app's link handling.
fn tab(cx: &mut TestAppContext) -> (Entity<TasksPane>, &mut VisualTestContext) {
    cx.update(gpui_kit::init);
    cx.update(crate::inline_field::init);
    cx.update(crate::core::navigation::open_url_handler::register);
    let (pane, cx) = cx.add_window_view(TasksPane::new);
    cx.update(|window, cx| {
        window.activate_window();
        cx.set_global(OpenPane(pane.downgrade()));
    });
    cx.run_until_parked();
    (pane, cx)
}

fn follow(url: &str, cx: &mut VisualTestContext) {
    cx.update(|window, cx| window.dispatch_action(Box::new(OpenUrl { url: url.into() }), cx));
    cx.run_until_parked();
}

fn opened_task(pane: &Entity<TasksPane>, cx: &mut VisualTestContext) -> Option<String> {
    cx.read(|cx| {
        let pane = pane.read(cx);
        pane.detail
            .as_ref()
            .and(pane.state.selected())
            .map(|source| source.page_id.clone())
    })
}

#[gpui::test]
fn with_notion_connected_a_notion_page_opens_its_task_here(cx: &mut TestAppContext) {
    let (pane, cx) = tab(cx);
    cx.update(|_, cx| pane.update(cx, |pane, cx| pane.settings_pushed(settings(true), cx)));
    let url = format!("https://acme.notion.site/Plan-{HEX}");
    follow(&url, cx);
    assert_eq!(opened_task(&pane, cx).as_deref(), Some(ID));
    assert_eq!(cx.opened_url(), None, "it didn't reach the browser");
}

#[gpui::test]
fn with_notion_not_connected_a_notion_page_opens_in_the_browser(cx: &mut TestAppContext) {
    let (pane, cx) = tab(cx);
    cx.update(|_, cx| pane.update(cx, |pane, cx| pane.settings_pushed(settings(false), cx)));
    let url = format!("http://www.notion.so/Plan-{HEX}");
    follow(&url, cx);
    assert_eq!(cx.opened_url().as_deref(), Some(url.as_str()));
    assert_eq!(opened_task(&pane, cx), None);
}

#[gpui::test]
fn with_notion_not_connected_a_task_link_opens_its_notion_page(cx: &mut TestAppContext) {
    let (pane, cx) = tab(cx);
    cx.update(|_, cx| pane.update(cx, |pane, cx| pane.settings_pushed(settings(false), cx)));
    follow(&format!("assembly://task/{ID}"), cx);
    let page = format!("https://app.notion.com/p/{HEX}");
    assert_eq!(cx.opened_url().as_deref(), Some(page.as_str()));
}

/// What the backend does with the tab's question about the settings.
#[derive(Clone, Copy, PartialEq)]
enum Settings {
    /// Held until `fixture/release`, then answered connected.
    Held,
    /// The connection drops instead of answering.
    Drops,
}

/// A backend that never pushes the settings, answers the settings question as `settings` says, and answers every page
/// read with a 404. Returns the page reads it was asked for.
fn backend(cx: &mut TestAppContext, answer: Settings) -> (FakeBackend, Arc<Mutex<Vec<Value>>>) {
    let asked = Arc::new(Mutex::new(Vec::new()));
    let details = asked.clone();
    let mut held: Option<Value> = None;
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        let id = &frame["id"];
        let result = |value: Value| Reply::Frame(json!({"t":"result","id":id,"result":value}));
        match (frame["t"].as_str(), frame["method"].as_str()) {
            (Some("call"), Some("view")) => vec![result(json!(NotionQuery::default()))],
            (Some("call"), Some("settings")) if answer == Settings::Drops => vec![Reply::Drop],
            (Some("call"), Some("settings")) => {
                let settings = serde_json::to_value(settings(true)).expect("settings");
                held = Some(json!({"t":"result","id":id,"result":settings}));
                vec![]
            }
            (Some("call"), Some("release")) => {
                let settings = held.take().expect("a held answer");
                vec![Reply::Frame(settings), result(Value::Null)]
            }
            (Some("call"), Some("detail")) => {
                details.lock().unwrap().push(frame["args"].clone());
                vec![Reply::Frame(
                    json!({"t":"error","id":id,"code":"failed","message":"notion_http_404"}),
                )]
            }
            (Some("call"), Some("ping")) => vec![result(Value::Null)],
            _ => vec![],
        }
    });
    start_sockets(backend.port, cx);
    (backend, asked)
}

fn start_sockets(port: u16, cx: &mut TestAppContext) {
    cx.executor().allow_parking();
    cx.update(|cx| {
        let endpoint = || Endpoint {
            port,
            token: "secret".into(),
        };
        SocketClient::start(endpoint(), cx);
        SocketClient::start_bulk(endpoint(), cx);
    });
}

async fn round_trip(method: &str, cx: &mut VisualTestContext) {
    cx.run_until_parked();
    let call = cx.update(|_, cx| {
        let socket = SocketClient::try_global(cx).expect("the socket");
        socket
            .read(cx)
            .call::<Value>("fixture", method, json!({}), None)
    });
    let _ = call.await;
    cx.run_until_parked();
}

// Before the tab knows whether Notion is connected, a link asks and waits. A second link replaces the first. Once the
// answer says connected, the link tries the task, and a page that isn't a task in the connected source opens in the
// browser, without coming back here.
#[gpui::test]
async fn a_link_before_the_connection_is_known_asks_and_a_newer_link_replaces_it(
    cx: &mut TestAppContext,
) {
    let (_backend, asked) = backend(cx, Settings::Held);
    let (pane, cx) = tab(cx);
    let first = "https://www.notion.so/Plan-22222222222222222222222222222222";
    let url = format!("https://www.notion.so/Plan-{HEX}");
    follow(first, cx);
    round_trip("ping", cx).await;
    follow(&url, cx);
    assert_eq!(cx.opened_url(), None, "nothing is decided yet");
    assert_eq!(opened_task(&pane, cx), None);
    round_trip("release", cx).await;
    round_trip("ping", cx).await;
    round_trip("ping", cx).await;
    let asked = asked.lock().unwrap().clone();
    assert_eq!(asked.len(), 1, "only the newer link was read: {asked:?}");
    assert_eq!(asked[0]["source"]["pageId"], ID);
    assert_eq!(cx.opened_url().as_deref(), Some(url.as_str()));
    assert_eq!(opened_task(&pane, cx), None, "it went back to the list");
}

// Review fix 2: with the backend down the question fails at once, and the page opens in the browser.
#[gpui::test]
async fn with_the_backend_down_a_link_opens_in_the_browser_without_waiting(
    cx: &mut TestAppContext,
) {
    start_sockets(crate::core::socket::fake_backend::unused_port(), cx);
    let (pane, cx) = tab(cx);
    let url = format!("https://www.notion.so/Plan-{HEX}");
    follow(&url, cx);
    // A call after it, which fails the same way, so the question has its answer by then.
    round_trip("ping", cx).await;
    let opened = cx.opened_url();
    assert_eq!(opened.as_deref(), Some(url.as_str()));
    assert_eq!(opened_task(&pane, cx), None);
}

// Review fix 2: a connection that drops while the link waits ends the wait, and the page opens in the browser.
#[gpui::test]
async fn a_connection_dropping_while_a_link_waits_opens_it_in_the_browser(cx: &mut TestAppContext) {
    let (_backend, asked) = backend(cx, Settings::Drops);
    let (pane, cx) = tab(cx);
    let url = format!("https://acme.notion.site/Plan-{HEX}");
    follow(&url, cx);
    round_trip("ping", cx).await;
    assert_eq!(cx.opened_url().as_deref(), Some(url.as_str()));
    assert_eq!(opened_task(&pane, cx), None);
    assert!(asked.lock().unwrap().is_empty());
}

/// The Tasks tab in a closed right dock, as the drawer has it.
fn docked(
    cx: &mut TestAppContext,
) -> (Entity<TasksPane>, Entity<DockArea>, &mut VisualTestContext) {
    cx.update(gpui_kit::init);
    cx.update(crate::inline_field::init);
    cx.update(crate::core::navigation::open_url_handler::register);
    let slot = std::rc::Rc::new(std::cell::OnceCell::new());
    let holder = slot.clone();
    let (_, cx) = cx.add_window_view(move |window, cx| {
        let dock = cx.new(|cx| DockArea::new("tasks-links", None, window, cx));
        CenterPanes::init(&dock, cx);
        let pane = cx.new(|cx| TasksPane::new(window, cx));
        cx.set_global(OpenPane(pane.downgrade()));
        dock.update(cx, |dock, cx| {
            dock.add_panel_view(
                panel_handle(pane.clone()),
                DockPlacement::Right,
                None,
                window,
                cx,
            );
            if dock.is_dock_open(DockPlacement::Right) {
                dock.toggle_dock(DockPlacement::Right, window, cx);
            }
        });
        let _ = holder.set((pane, dock.clone()));
        gpui_kit::base::Root::new(dock, window, cx)
    });
    cx.update(|window, _| window.activate_window());
    cx.run_until_parked();
    let (pane, dock) = slot.get().expect("the tab").clone();
    (pane, dock, cx)
}

// Review fix 3: the tab comes forward only for a task; a Notion page with no connection goes to the browser alone.
#[gpui::test]
fn the_tab_comes_forward_only_when_it_opens_a_task(cx: &mut TestAppContext) {
    let (pane, dock, cx) = docked(cx);
    let open =
        |cx: &mut VisualTestContext| cx.read(|cx| dock.read(cx).is_dock_open(DockPlacement::Right));
    cx.update(|_, cx| pane.update(cx, |pane, cx| pane.settings_pushed(settings(false), cx)));
    let url = format!("https://www.notion.so/Plan-{HEX}");
    follow(&url, cx);
    assert_eq!(cx.opened_url().as_deref(), Some(url.as_str()));
    assert!(!open(cx), "not connected: the drawer stays shut");
    cx.update(|_, cx| pane.update(cx, |pane, cx| pane.settings_pushed(settings(true), cx)));
    follow(&format!("assembly://task/{ID}"), cx);
    assert_eq!(opened_task(&pane, cx).as_deref(), Some(ID));
    assert!(open(cx), "a task brings the tab forward");
}

//! Feature 28 parity item 3: a Notion page link from anywhere opens its task in the Tasks tab when Notion is connected,
//! and in the browser when it isn't or when the page isn't a task in the connected source.

use std::sync::{Arc, Mutex};

use gpui::TestAppContext;
use gpui_kit::{Entity, VisualTestContext};
use serde_json::{Value, json};

use super::{OpenPane, TasksPane};
use crate::core::intents::open_url::OpenUrl;
use crate::core::socket::client::SocketClient;
use crate::core::socket::endpoint::Endpoint;
use crate::core::socket::fake_backend::{FakeBackend, Reply, hello};
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

/// A backend whose `notion/view` answer waits for `fixture/release`, whose settings say connected, and whose page reads
/// all answer 404. Returns the detail reads it was asked for.
fn backend(cx: &mut TestAppContext) -> (FakeBackend, Arc<Mutex<Vec<Value>>>) {
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
            (Some("sub"), _) if frame["topic"] == "notion-settings" => {
                let item = serde_json::to_value(settings(true)).expect("settings");
                vec![Reply::Frame(
                    json!({"t":"state","sub":id,"items":[{"key":"settings","item":item}]}),
                )]
            }
            (Some("call"), Some("view")) => {
                held = Some(json!({"t":"result","id":id,"result":NotionQuery::default()}));
                vec![]
            }
            (Some("call"), Some("release")) => {
                let view = held.take().expect("a held view");
                vec![Reply::Frame(view), result(Value::Null)]
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
    cx.executor().allow_parking();
    cx.update(|cx| {
        let endpoint = || Endpoint {
            port: backend.port,
            token: "secret".into(),
        };
        SocketClient::start(endpoint(), cx);
        SocketClient::start_bulk(endpoint(), cx);
    });
    (backend, asked)
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

// Before the tab knows whether Notion is connected, a link waits for it, then tries the task, and a page that isn't a
// task in the connected source opens in the browser, without coming back here.
#[gpui::test]
async fn a_link_before_the_connection_is_known_waits_for_it_then_falls_back_once(
    cx: &mut TestAppContext,
) {
    let (_backend, asked) = backend(cx);
    let (pane, cx) = tab(cx);
    let url = format!("https://www.notion.so/Plan-{HEX}");
    follow(&url, cx);
    assert_eq!(cx.opened_url(), None, "nothing is decided yet");
    assert_eq!(opened_task(&pane, cx), None);
    round_trip("release", cx).await;
    round_trip("ping", cx).await;
    round_trip("ping", cx).await;
    let asked = asked.lock().unwrap().clone();
    assert_eq!(asked.len(), 1, "the task was read once: {asked:?}");
    assert_eq!(asked[0]["source"]["pageId"], ID);
    assert_eq!(cx.opened_url().as_deref(), Some(url.as_str()));
    assert_eq!(opened_task(&pane, cx), None, "it went back to the list");
}

//! Spec 7: a `file://` OSC 8 link on the Terminal tab opens in the app, as a file on the session's machine, through
//! feature 28's `OpenUrl` handler.

use std::cell::RefCell;
use std::rc::Rc;

use gpui::TestAppContext;
use gpui_kit::component::WindowExt as _;
use gpui_kit::{AppContext as _, EmptyView, Modifiers};
use serde_json::{Value, json};

use super::mouse::at;
use super::{Open, Terminals, following, open};
use crate::core::engine::SessionKey;
use crate::core::intents::open_url::OpenUrl;
use crate::core::navigation::deep_links::notices;
use crate::core::navigation::open_url_handler;
use crate::core::socket::client::SocketClient;
use crate::core::socket::row_source::ExpectedRowSource;
use crate::core::state::selected_session::SelectedSession;

const SHOWN: &str = "main.rs";

/// The tab on session a, opened from `route` as the navigator opens it, following a shell that printed `SHOWN` as an
/// OSC 8 link to `url`.
async fn printed<'a>(
    cx: &'a mut TestAppContext,
    url: &str,
    route: Option<&str>,
    terminals: Terminals,
) -> Open<'a> {
    let kept = format!("\x1b]8;;{url}\x1b\\{SHOWN}\x1b]8;;\x1b\\");
    let terminals = Terminals {
        kept: kept.into_bytes(),
        ..terminals
    };
    let mut open = open(cx, terminals, false).await;
    let key = SessionKey {
        machine: "owner".into(),
        id: "a".into(),
    };
    let source = ExpectedRowSource {
        machine: "owner".into(),
        generation: u64::from(route.is_some()),
        route_machine: route.map(String::from),
    };
    open.cx.update(|_, cx| {
        SelectedSession::global(cx).update(cx, |selected, cx| {
            selected.commit_open(key, source, false, cx)
        })
    });
    open.until("the output", |_, pane| {
        following(pane) && pane.grid.text() == SHOWN
    })
    .await;
    open
}

/// What `OpenUrl` is asked to open from now on.
fn recorded(open: &mut Open<'_>) -> Rc<RefCell<Vec<String>>> {
    let opened = Rc::new(RefCell::new(Vec::new()));
    let record = opened.clone();
    open.cx.update(|_, cx| {
        cx.on_action(move |intent: &OpenUrl, _| record.borrow_mut().push(intent.url.clone()));
    });
    opened
}

/// Cmd or Ctrl-click on the link.
fn click(open: &mut Open<'_>) {
    let position = at(open, 1.5, 0.5);
    let modifiers = open.cx.update(|_, cx| Modifiers::secondary_key(cx));
    open.cx.simulate_click(position, modifiers);
    open.cx.run_until_parked();
}

#[gpui::test]
async fn a_terminal_file_link_uses_the_sessions_machine(cx: &mut TestAppContext) {
    let url = "file://box/work/a/src/main.rs";
    let mut open = printed(cx, url, Some("Workbox Test"), Terminals::default()).await;
    let opened = recorded(&mut open);
    let position = at(&mut open, 1.5, 0.5);
    open.cx
        .simulate_mouse_move(position, None, Modifiers::none());
    open.cx.run_until_parked();
    let spans = (open.pane).read_with(open.cx, |pane, _| {
        pane.hovered.as_ref().map(|link| link.line_spans.clone())
    });
    assert_eq!(
        spans,
        Some(vec![(0, 0, SHOWN.len())]),
        "the link underlines"
    );
    click(&mut open);
    assert_eq!(
        *opened.borrow(),
        vec!["assembly://file?path=%2Fwork%2Fa%2Fsrc%2Fmain.rs&profile=Workbox%20Test".to_string()]
    );
}

#[gpui::test]
async fn none_when_local(cx: &mut TestAppContext) {
    let url = "file://my-mac.local/work/a/my%20file.rs";
    let mut open = printed(cx, url, None, Terminals::default()).await;
    let opened = recorded(&mut open);
    click(&mut open);
    assert_eq!(
        *opened.borrow(),
        vec!["assembly://file?path=%2Fwork%2Fa%2Fmy%20file.rs".to_string()]
    );
}

#[gpui::test]
async fn a_path_outside_the_folder_gets_the_handlers_notice(cx: &mut TestAppContext) {
    let owner = json!({"machine": "owner", "checkoutRoot": "/work/a", "remoteSessionId": "r"});
    let terminals = Terminals {
        answers: vec![("owner".into(), owner)],
        ..Default::default()
    };
    let mut open = printed(cx, "file://box/etc/passwd", None, terminals).await;
    // The handler's notice goes to a window with the kit's root, as the app's is.
    open.cx.update(|_, cx| open_url_handler::register(cx));
    let root = open.cx.add_window(|window, cx| {
        let content = cx.new(|_| EmptyView);
        gpui_kit::base::Root::new(content, window, cx)
    });
    _ = root.update(open.cx, |_, window, _| window.activate_window());
    click(&mut open);
    // One round trip after the click: the backend's answer to the handler's call has reached the app.
    let client = open
        .cx
        .update(|_, cx| SocketClient::try_global(cx).expect("a socket"));
    let ping = open.cx.update(|_, cx| {
        client
            .read(cx)
            .call::<Value>("fixture", "ping", json!({}), None)
    });
    _ = ping.await;
    open.cx.run_until_parked();
    assert_eq!(
        open.cx.update(|_, cx| notices(cx)),
        vec!["That file isn't in this session's folder.".to_string()]
    );
    let asked = open.terminals.lock().unwrap().of("owner");
    assert_eq!(asked.len(), 1);
    assert_eq!(asked[0]["session"], "a");
    let notes =
        (open.cx).update_window(root.into(), |_, window, cx| window.notifications(cx).len());
    assert_eq!(notes.ok(), Some(1));
}

//! The theme and interface size Settings saved are in force from the window's first frame, even when they arrived
//! before the window was made.

use futures_channel::mpsc::unbounded;
use gpui_kit::component::Theme;
use gpui_kit::{
    AppContext as _, Context, IntoElement, Render, TestAppContext, Window, div, gpui, px,
};
use serde_json::json;

use super::WindowRoot;
use crate::core::socket::client::SocketClient;
use crate::core::socket::endpoint::Endpoint;
use crate::core::socket::fake_backend::{FakeBackend, Reply, hello};
use crate::core::socket::test_wait::until;
use crate::core::state::appearance::UI_FONT_SIZE;
use crate::core::state::preferences::Preferences;
use crate::core::theme::theme_id::ThemeId;
use crate::core::theme::{self, THEMES_DIR};

struct Blank;

impl Render for Blank {
    fn render(&mut self, _window: &mut Window, _cx: &mut Context<Self>) -> impl IntoElement {
        div()
    }
}

#[gpui::test]
async fn preferences_loaded_before_the_root_is_made_are_in_force(cx: &mut TestAppContext) {
    let backend = FakeBackend::start(|frame| {
        if let Some(replies) = hello(frame) {
            return replies;
        }
        match frame["t"].as_str() {
            Some("sub") => vec![Reply::Frame(
                json!({"t": "state", "sub": frame["id"], "items": [
                {"key": "appearance.theme", "item": {"key": "appearance.theme", "value": "houston"}},
                {"key": "appearance.uiFontSize", "item": {"key": "appearance.uiFontSize", "value": 16}}]}),
            )],
            _ => Vec::new(),
        }
    });
    cx.executor().allow_parking();
    let preferences = cx.update(|cx| {
        gpui_kit::init(cx);
        theme::init(THEMES_DIR.into(), cx, |_| {});
        let endpoint = Endpoint {
            port: backend.port,
            token: "secret".into(),
        };
        SocketClient::start(endpoint, cx);
        Preferences::init(cx);
        Preferences::start(cx);
        Preferences::try_global(cx).expect("the preferences")
    });
    let (changed, mut changes) = unbounded();
    let tell = changed.clone();
    let _heard = cx.update(|cx| cx.observe(&preferences, move |_, _| _ = tell.unbounded_send(())));
    until("the saved state", &mut changes, &changed, || {
        cx.update(|cx| Preferences::read(cx, |appearance| appearance.number(&UI_FONT_SIZE))) == 16.
    })
    .await;

    // As main.rs opens it: in the default theme, inside the kit's root.
    let mut made = None;
    let (_, window) = cx.add_window_view(|window, cx| {
        theme::set(ThemeId::DEFAULT, window, cx);
        let content = cx.new(|_| Blank);
        let root = cx.new(|cx| WindowRoot::new(content, window, cx));
        made = Some(root.clone());
        gpui_kit::base::Root::new(root, window, cx)
    });
    window.run_until_parked();
    let root = made.expect("the root");
    root.read_with(window, |root, _| assert_eq!(root.theme, ThemeId::Houston));
    window.update(|_, cx| assert_eq!(Theme::global(cx).font_size, px(16.)));
}

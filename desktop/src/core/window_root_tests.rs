//! The theme and interface size Settings saved are in force from the window's first frame, even when they arrived
//! before the window was made.

use std::cell::Cell;
use std::rc::Rc;
use std::sync::{Arc, Mutex};

use futures_channel::mpsc::unbounded;
use gpui_kit::component::{Theme, ThemeRegistry};
use gpui_kit::{
    AnyView, AppContext as _, Context, IntoElement, ParentElement as _, Render, StyleRefinement,
    TestAppContext, VisualTestContext, Window, div, gpui, px,
};
use serde_json::{Value, json};

use super::WindowRoot;
use crate::core::socket::client::SocketClient;
use crate::core::socket::endpoint::Endpoint;
use crate::core::socket::fake_backend::{FakeBackend, Reply, hello};
use crate::core::socket::test_wait::until;
use crate::core::theme::copied_themes::copied_themes;
use crate::core::theme::text::{TextRole, WINDOW_REM};
use crate::core::state::appearance::{EDITOR_FONT_SIZE, THEME, UI_FONT_SIZE};
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
        cx.update(|cx| Preferences::read(cx, |appearance| appearance.theme())) == ThemeId::Houston
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

/// Says the backend's state has arrived, also for a profile with nothing saved.
const ARRIVED: &str = "test.arrived";

/// The window as main.rs opens it, on a backend holding `saved` as (key, value), which counts the saves it's sent.
async fn open_with<'a>(
    saved: &[(&str, Value)],
    cx: &'a mut TestAppContext,
) -> (FakeBackend, Arc<Mutex<u32>>, &'a mut VisualTestContext) {
    let items: Vec<Value> = saved
        .iter()
        .chain(&[(ARRIVED, json!(true))])
        .map(|(key, value)| json!({"key": key, "item": {"key": key, "value": value}}))
        .collect();
    let sets = Arc::new(Mutex::new(0));
    let counted = sets.clone();
    let backend = FakeBackend::start(move |frame| {
        if let Some(replies) = hello(frame) {
            return replies;
        }
        match (frame["t"].as_str(), frame["method"].as_str()) {
            (Some("sub"), _) => vec![Reply::Frame(
                json!({"t": "state", "sub": frame["id"], "items": items}),
            )],
            (Some("call"), Some("set")) => {
                *counted.lock().unwrap() += 1;
                Vec::new()
            }
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
        cx.update(|cx| Preferences::value(ARRIVED, cx).is_some())
    })
    .await;
    let (_, window) = cx.add_window_view(|window, cx| {
        theme::set(ThemeId::DEFAULT, window, cx);
        let content = cx.new(|_| Blank);
        let root = cx.new(|cx| WindowRoot::new(content, window, cx));
        gpui_kit::base::Root::new(root, window, cx)
    });
    window.run_until_parked();
    (backend, sets, window)
}

/// Reloads a copy of the real theme files, each saying `font.size` 16, as the kit does when one changes on disk.
/// The watcher itself reports from its own OS thread, which the test scheduler can't take, so the kit is pointed at the
/// copy: that runs the same reload and re-apply (apply.rs's reload test says the same).
fn reload_themes(name: &str, window: &mut VisualTestContext) {
    let dir = copied_themes(THEMES_DIR, name);
    window.update(|_, cx| ThemeRegistry::watch_dir(dir, cx, |_| {}).expect("reload the copy"));
    window.run_until_parked();
}

/// The size our text draws at, which follows the saved size while rem stays put.
fn text_size(window: &mut VisualTestContext) -> gpui_kit::Pixels {
    window.update(|_, cx| Preferences::read(cx, |appearance| appearance.ui_text(TextRole::Chat).0))
}

/// The size the theme holds and the rem size the next frame draws at, which sizes `text_sm`, `text_xs` and the kit.
fn drawn_size(window: &mut VisualTestContext) -> (gpui_kit::Pixels, gpui_kit::Pixels) {
    window.update(|window, _| window.refresh());
    window.run_until_parked();
    window.update(|window, cx| (Theme::global(cx).font_size, window.rem_size()))
}

#[gpui::test]
async fn a_theme_reload_keeps_a_saved_size_of_11(cx: &mut TestAppContext) {
    let (_backend, sets, window) = open_with(&[(UI_FONT_SIZE.key, json!(11))], cx).await;
    reload_themes("size-11", window);
    assert_eq!(drawn_size(window), (WINDOW_REM, WINDOW_REM));
    assert_eq!(text_size(window), px(11.));
    assert_eq!(*sets.lock().unwrap(), 0, "a reload wrote a preference");
}

#[gpui::test]
async fn a_theme_reload_keeps_a_saved_size_of_18_and_the_saved_face(cx: &mut TestAppContext) {
    let saved = [
        (UI_FONT_SIZE.key, json!(18)),
        ("appearance.uiFont", json!("Inter")),
    ];
    let (_backend, sets, window) = open_with(&saved, cx).await;
    reload_themes("size-18", window);
    assert_eq!(drawn_size(window), (WINDOW_REM, WINDOW_REM));
    assert_eq!(text_size(window), px(18.));
    window.update(|_, cx| assert_eq!(Theme::global(cx).font_family.as_ref(), "Inter"));
    assert_eq!(*sets.lock().unwrap(), 0, "a reload wrote a preference");
}

#[gpui::test]
async fn a_saved_size_of_13_stays_13_though_the_default_is_larger(cx: &mut TestAppContext) {
    let (_backend, _sets, window) = open_with(&[(UI_FONT_SIZE.key, json!(13))], cx).await;
    reload_themes("size-13", window);
    assert_eq!(drawn_size(window), (WINDOW_REM, WINDOW_REM));
    assert_eq!(text_size(window), px(13.));
}

#[gpui::test]
async fn a_profile_with_no_saved_size_draws_at_16_and_keeps_it_over_a_reload(
    cx: &mut TestAppContext,
) {
    let (_backend, sets, window) = open_with(&[], cx).await;
    assert_eq!(drawn_size(window), (px(16.), px(16.)));
    reload_themes("default", window);
    assert_eq!(drawn_size(window), (px(16.), px(16.)));
    assert_eq!(*sets.lock().unwrap(), 0, "a reload wrote a preference");
}

#[gpui::test]
async fn a_reload_settles_after_one_correction(cx: &mut TestAppContext) {
    let (_backend, _sets, window) = open_with(&[(UI_FONT_SIZE.key, json!(16))], cx).await;
    let updates = Rc::new(Cell::new(0));
    let count = updates.clone();
    let _watch = window.update(|_, cx| {
        cx.observe_global::<Theme>(move |_| count.set(count.get() + 1))
    });
    reload_themes("settles", window);
    let after_reload = updates.get();
    assert!(after_reload > 0, "the reload didn't touch the theme");
    assert!(after_reload <= 4, "{after_reload} theme updates for one reload");
    // A redraw with nothing changed updates nothing.
    assert_eq!(drawn_size(window), (px(16.), px(16.)));
    assert_eq!(updates.get(), after_reload, "the theme kept updating");
}

#[gpui::test]
async fn an_unrelated_preference_and_a_theme_switch_keep_the_saved_size(cx: &mut TestAppContext) {
    let (_backend, _sets, window) = open_with(&[(UI_FONT_SIZE.key, json!(16))], cx).await;
    window.update(|_, cx| Preferences::save(EDITOR_FONT_SIZE.key, json!(20), cx));
    window.run_until_parked();
    assert_eq!(drawn_size(window), (px(16.), px(16.)));
    window.update(|_, cx| Preferences::save(THEME, json!("dracula"), cx));
    window.run_until_parked();
    window.update(|_, cx| assert_eq!(Theme::global(cx).theme_name().as_ref(), "Dracula"));
    assert_eq!(drawn_size(window), (px(16.), px(16.)));
}

/// Counts its renders and, like a panel in the dock, doesn't observe the preferences.
struct UnobservingPanel(Rc<Cell<u32>>);

impl Render for UnobservingPanel {
    fn render(&mut self, _window: &mut Window, _cx: &mut Context<Self>) -> impl IntoElement {
        self.0.set(self.0.get() + 1);
        div()
    }
}

/// Draws the panel cached, as the dock does, so only an invalidation reaches it.
struct Dock(AnyView);

impl Render for Dock {
    fn render(&mut self, _window: &mut Window, _cx: &mut Context<Self>) -> impl IntoElement {
        div().child(self.0.clone().cached(StyleRefinement::default()))
    }
}

/// Draws the window, then says how many times the panel has rendered.
fn renders_after_a_draw(window: &mut VisualTestContext, renders: &Cell<u32>) -> u32 {
    window.run_until_parked();
    window.update(|window, cx| window.draw(cx).clear(cx));
    window.run_until_parked();
    renders.get()
}

#[gpui::test]
async fn only_a_new_interface_size_redraws_a_cached_panel(cx: &mut TestAppContext) {
    let (_backend, _sets, window) = open_with(&[(UI_FONT_SIZE.key, json!(16))], cx).await;
    let renders = Rc::new(Cell::new(0));
    let counter = renders.clone();
    let (_, window) = window.add_window_view(|window, cx| {
        let panel = cx.new(|_| UnobservingPanel(counter));
        let dock = cx.new(|_| Dock(panel.into()));
        let root = cx.new(|cx| WindowRoot::new(dock, window, cx));
        gpui_kit::base::Root::new(root, window, cx)
    });
    let before = renders_after_a_draw(window, &renders);
    assert!(before > 0, "the panel never drew");
    assert_eq!(
        renders_after_a_draw(window, &renders),
        before,
        "the panel isn't cached"
    );

    window.update(|_, cx| Preferences::save(EDITOR_FONT_SIZE.key, json!(20), cx));
    assert_eq!(
        renders_after_a_draw(window, &renders),
        before,
        "an unrelated preference redrew it"
    );

    window.update(|_, cx| Preferences::save(UI_FONT_SIZE.key, json!(18), cx));
    let after = renders_after_a_draw(window, &renders);
    assert!(after > before, "the new size didn't reach a cached panel");

    window.update(|_, cx| Preferences::save(UI_FONT_SIZE.key, json!(18), cx));
    assert_eq!(
        renders_after_a_draw(window, &renders),
        after,
        "the same size redrew it"
    );
}

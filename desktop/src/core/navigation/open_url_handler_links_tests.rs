//! Spec section 6: an `assembly://` link clicked inside the app does what it does from outside.

use gpui::TestAppContext;
use gpui_kit::component::WindowExt as _;
use gpui_kit::{AppContext as _, EmptyView, VisualTestContext};

use super::register;
use crate::core::intents::open_url::OpenUrl;
use crate::core::state::selected_session::SelectedSession;

fn window(cx: &mut TestAppContext) -> &mut VisualTestContext {
    cx.update(|cx| {
        gpui_kit::init(cx);
        SelectedSession::init(cx);
        register(cx);
    });
    let (_, cx) = cx.add_window_view(|window, cx| {
        let content = cx.new(|_| EmptyView);
        gpui_kit::base::Root::new(content, window, cx)
    });
    cx.update(|window, _| window.activate_window());
    cx.run_until_parked();
    cx
}

fn follow(url: &str, cx: &mut VisualTestContext) {
    cx.update(|window, cx| window.dispatch_action(Box::new(OpenUrl { url: url.into() }), cx));
    cx.run_until_parked();
}

fn notices(cx: &mut VisualTestContext) -> usize {
    cx.update(|window, cx| window.notifications(cx).len())
}

#[gpui::test]
fn a_clicked_open_link_opens_its_web_page(cx: &mut TestAppContext) {
    let cx = window(cx);
    follow("assembly://open?url=https%3A%2F%2Fa.test%2Fb", cx);
    assert_eq!(cx.opened_url().as_deref(), Some("https://a.test/b"));
    assert_eq!(notices(cx), 0);
}

#[gpui::test]
fn a_clicked_session_link_is_not_refused_and_never_reaches_the_system(cx: &mut TestAppContext) {
    let cx = window(cx);
    follow("assembly://session/s1", cx);
    assert_eq!(cx.opened_url(), None);
    assert_eq!(notices(cx), 0);
}

#[gpui::test]
fn a_clicked_link_that_does_not_parse_keeps_the_refusal(cx: &mut TestAppContext) {
    let cx = window(cx);
    for (at, address) in [
        "assembly://session/",
        "assembly://open",
        "assembly://nothing",
        "file:///etc/passwd",
    ]
    .into_iter()
    .enumerate()
    {
        follow(address, cx);
        assert_eq!(cx.opened_url(), None, "{address}");
        assert_eq!(notices(cx), at + 1, "{address}");
    }
}

// Proof fixes brief item 5: a relative file link is refused in the file words, not as a link that isn't a web page.
#[gpui::test]
fn a_clicked_relative_file_link_says_it_is_not_a_full_path(cx: &mut TestAppContext) {
    let cx = window(cx);
    for address in [
        "assembly://file?path=src%2Fmain.rs&profile=workbox-test",
        "assembly://file?path=relative.cs",
    ] {
        follow(address, cx);
        assert_eq!(cx.opened_url(), None, "{address}");
        let shown = cx.update(|_, cx| crate::core::navigation::deep_links::notices(cx));
        assert_eq!(
            shown.last().map(String::as_str),
            Some("That file link isn't a full path."),
            "{address}"
        );
    }
}

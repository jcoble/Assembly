//! The link handler sends web pages to the system and nothing else.

use gpui::TestAppContext;
use gpui_kit::component::WindowExt as _;
use gpui_kit::{AppContext as _, EmptyView, VisualTestContext};

use super::{OpenUrl, register};

fn window(cx: &mut TestAppContext) -> &mut VisualTestContext {
    cx.update(|cx| {
        gpui_kit::init(cx);
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

#[gpui::test]
fn a_web_page_opens_exactly_as_written(cx: &mut TestAppContext) {
    let url = "https://GitHub.com/o/r/pull/7?a=b%20c&d=%2F#files";
    let cx = window(cx);
    follow(url, cx);
    assert_eq!(cx.opened_url().as_deref(), Some(url));
    cx.update(|window, cx| assert_eq!(window.notifications(cx).len(), 0));
}

#[gpui::test]
fn any_other_address_never_reaches_the_system_and_says_so(cx: &mut TestAppContext) {
    let cx = window(cx);
    let refused = [
        "file:///etc/passwd",
        "data:text/html,hi",
        "javascript:alert(1)",
        "vscode://open",
        "assembly://open",
        "/home/me/notes.md",
        "github.com/o/r",
        " https://github.com/o/r",
        "ht\ntps://github.com/o/r",
        "https://github.com/o/r\tx",
    ];
    for address in refused {
        follow(address, cx);
        assert_eq!(cx.opened_url(), None, "{address} reached the system");
    }
    cx.update(|window, cx| assert_eq!(window.notifications(cx).len(), refused.len()));
}

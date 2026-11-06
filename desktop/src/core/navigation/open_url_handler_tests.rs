//! The link handler sends web pages to the system and nothing else, except the pages the app opens itself.

use gpui::TestAppContext;
use gpui_kit::component::WindowExt as _;
use gpui_kit::{AppContext as _, EmptyView, VisualTestContext};

use std::cell::RefCell;
use std::rc::Rc;

use super::register;
use crate::core::intents::open_pull_requests::OpenPullRequests;
use crate::core::intents::open_url::{OpenInBrowser, OpenUrl};

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
    let url = "https://GitHub.com/o/r/issues/7?a=b%20c&d=%2F#files";
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

/// The pull requests the router asked the Pull requests tab to open.
type Heard = Rc<RefCell<Vec<(Option<String>, Option<u64>)>>>;

fn heard_pull_requests(cx: &mut VisualTestContext) -> Heard {
    let heard = Rc::new(RefCell::new(Vec::new()));
    let into = heard.clone();
    cx.update(|_, cx| {
        cx.on_action(move |intent: &OpenPullRequests, _| {
            assert_eq!(intent.session_id, None);
            into.borrow_mut().push((intent.repo.clone(), intent.number));
        });
    });
    heard
}

// Feature 28 parity item 2: a GitHub pull request link opens in the Pull requests tab, by the old app's rules.
#[gpui::test]
fn a_pull_request_link_opens_the_pull_requests_tab_and_not_the_browser(cx: &mut TestAppContext) {
    let cx = window(cx);
    let heard = heard_pull_requests(cx);
    for url in [
        "https://github.com/o/r/pull/7",
        "http://www.github.com/o/r/pull/8/files#diff",
        "assembly://open?url=https%3A%2F%2Fgithub.com%2Fo%2Fr%2Fpull%2F9",
    ] {
        follow(url, cx);
        assert_eq!(cx.opened_url(), None, "{url} reached the browser");
    }
    let wanted = [7, 8, 9].map(|number| (Some("o/r".to_string()), Some(number)));
    assert_eq!(*heard.borrow(), wanted);
    cx.update(|window, cx| assert_eq!(window.notifications(cx).len(), 0));
}

#[gpui::test]
fn other_github_pages_still_open_in_the_browser(cx: &mut TestAppContext) {
    let cx = window(cx);
    let heard = heard_pull_requests(cx);
    for url in [
        "https://github.com/o/r/issues/7",
        "https://github.com/o/r/commit/abc",
        "https://github.com/o/r/pull/0",
    ] {
        follow(url, cx);
        assert_eq!(cx.opened_url().as_deref(), Some(url));
    }
    assert!(heard.borrow().is_empty());
}

// Item 3: with no Tasks tab to open it in, a Notion page opens in the browser, as the old app did when it wasn't a task.
#[gpui::test]
fn a_notion_page_with_no_tasks_tab_opens_in_the_browser(cx: &mut TestAppContext) {
    let cx = window(cx);
    let url = "https://acme.notion.site/Plan-11111111111111111111111111111111";
    follow(url, cx);
    assert_eq!(cx.opened_url().as_deref(), Some(url));
    cx.update(|window, cx| assert_eq!(window.notifications(cx).len(), 0));
}

// Item 5: an email link goes to the system, which hands it to the mail app.
#[gpui::test]
fn a_mail_link_goes_to_the_mail_app(cx: &mut TestAppContext) {
    let cx = window(cx);
    follow("mailto:someone@example.com", cx);
    assert_eq!(
        cx.opened_url().as_deref(),
        Some("mailto:someone@example.com")
    );
    cx.update(|window, cx| assert_eq!(window.notifications(cx).len(), 0));
}

#[gpui::test]
fn a_mail_link_holding_a_space_or_newline_is_refused(cx: &mut TestAppContext) {
    let cx = window(cx);
    for address in ["mailto:a@b.test\nbcc:x@y.test", "mailto:a@b.test x"] {
        follow(address, cx);
        assert_eq!(cx.opened_url(), None, "{address}");
    }
}

// "Open on GitHub" means the browser, even for the pull request the router would open in the app.
#[gpui::test]
fn open_in_browser_skips_the_app_s_own_pages(cx: &mut TestAppContext) {
    let cx = window(cx);
    let heard = heard_pull_requests(cx);
    let open = |url: &str, cx: &mut VisualTestContext| {
        cx.update(|window, cx| {
            window.dispatch_action(Box::new(OpenInBrowser { url: url.into() }), cx)
        });
        cx.run_until_parked();
    };
    open("vscode://x", cx);
    assert_eq!(cx.opened_url(), None);
    cx.update(|window, cx| assert_eq!(window.notifications(cx).len(), 1));
    let url = "https://github.com/o/r/pull/7";
    open(url, cx);
    assert_eq!(cx.opened_url().as_deref(), Some(url));
    assert!(heard.borrow().is_empty());
}

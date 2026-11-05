//! Spec sections 3 and 5: an open link goes where web links go, and what the system hands over goes through the queue.

use std::cell::RefCell;

use gpui::TestAppContext;
use gpui_kit::component::WindowExt as _;
use gpui_kit::{AppContext as _, EmptyView, VisualTestContext};

use super::deep_links::handle_route;
use super::external_urls::{ExternalRoute, ExternalUrls};

fn window(cx: &mut TestAppContext) -> &mut VisualTestContext {
    cx.update(|cx| {
        gpui_kit::init(cx);
        crate::core::intents::open_url::register(cx);
    });
    let (_, cx) = cx.add_window_view(|window, cx| {
        let content = cx.new(|_| EmptyView);
        gpui_kit::base::Root::new(content, window, cx)
    });
    cx.update(|window, _| window.activate_window());
    cx.run_until_parked();
    cx
}

#[gpui::test]
fn an_open_link_goes_where_web_links_go(cx: &mut TestAppContext) {
    let cx = window(cx);
    let url = "https://GitHub.com/o/r/pull/7?a=b%20c";
    let route = ExternalRoute::Open { url: url.into() };
    // As the queue does: outside any window update.
    cx.update(|_, cx| cx.defer(move |cx| handle_route(route, cx)));
    cx.run_until_parked();
    assert_eq!(cx.opened_url().as_deref(), Some(url));
    cx.update(|window, cx| assert_eq!(window.notifications(cx).len(), 0));
}

#[gpui::test]
fn an_unsupported_link_still_says_so(cx: &mut TestAppContext) {
    let cx = window(cx);
    cx.update(|_, cx| cx.defer(|cx| handle_route(ExternalRoute::Unsupported, cx)));
    cx.run_until_parked();
    cx.update(|window, cx| assert_eq!(window.notifications(cx).len(), 1));
}

thread_local! {
    static HANDED: RefCell<Vec<ExternalRoute>> = const { RefCell::new(Vec::new()) };
}

fn record(route: ExternalRoute, _: &mut gpui_kit::App) {
    HANDED.with(|handed| handed.borrow_mut().push(route));
}

#[gpui::test]
fn the_queue_turns_a_file_from_the_system_into_a_file_link(cx: &mut TestAppContext) {
    HANDED.with(|handed| handed.borrow_mut().clear());
    let urls = ExternalUrls::new();
    urls.receive(vec![
        "file:///work/a.cs".into(),
        "assembly://session/s1".into(),
    ]);
    cx.update(|cx| urls.start(record, cx));
    cx.run_until_parked();
    assert_eq!(
        HANDED.with(|handed| handed.borrow().clone()),
        [
            ExternalRoute::File {
                path: "/work/a.cs".into(),
                machine: None
            },
            ExternalRoute::Session {
                id: "s1".into(),
                machine: None
            },
        ]
    );
}

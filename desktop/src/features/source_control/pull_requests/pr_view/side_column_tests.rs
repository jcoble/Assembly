//! The side column stays put beside the page: a wheel there moves the page unless the column has more of its own.
use super::super::super::{pr_owner::PrOwner, pull_request::PullRequestDetail};
use super::super::PrDetail;
use gpui::TestAppContext;
use gpui_kit::{Entity, ScrollDelta, ScrollEvent, VisualTestContext, point, px, size};
use serde_json::json;

fn page(cx: &mut TestAppContext, checks: usize) -> (Entity<PrDetail>, &mut VisualTestContext) {
    cx.update(gpui_kit::init);
    let commits: Vec<_> = (0..80)
        .map(|_| json!({"oid":"aaaaaaa","headline":"A change","author":"amy","committedAt":"2026-01-01T00:00:00Z"}))
        .collect();
    let checks: Vec<_> = (0..checks)
        .map(|_| json!({"name":"ci","status":"completed","conclusion":"success","url":""}))
        .collect();
    let detail: PullRequestDetail = serde_json::from_value(json!({"repository":"o/r","localRoot":"/r","number":7,"title":"PR","body":"","bodyBlocks":[],"url":"","author":"amy","headBranch":"f","baseBranch":"main","headSha":"a","baseSha":"b","state":"MERGED","isDraft":false,"mergeable":"MERGEABLE","reviewDecision":"","createdAt":"","mergedAt":"","mergedBy":"","closedAt":"","commits":commits,"comments":[],"reviews":[],"reviewers":[],"checks":checks,"files":[],"moreFiles":false,"timeline":[]})).unwrap();
    let (page, visual) = cx.add_window_view(move |_, cx| {
        let owner = PrOwner::Session {
            id: "session".into(),
            source: None,
        };
        let mut page = PrDetail::new(owner, Some("o/r".into()), 7, false, cx);
        page.detail = Some(detail);
        page
    });
    visual.simulate_resize(size(px(1400.), px(500.)));
    visual.run_until_parked();
    visual.update(|window, cx| window.draw(cx).clear(cx));
    (page, visual)
}
fn wheel_over_the_side_column(visual: &mut VisualTestContext) {
    let side = visual
        .debug_bounds("pr-summary-side")
        .expect("the side column");
    visual.simulate_event(ScrollEvent {
        position: side.center(),
        delta: ScrollDelta::Pixels(point(px(0.), px(-60.))),
        ..ScrollEvent::default()
    });
    visual.run_until_parked();
}
#[gpui::test]
fn a_wheel_over_a_short_side_column_moves_the_page(cx: &mut TestAppContext) {
    let (page, visual) = page(cx, 0);
    wheel_over_the_side_column(visual);
    visual.update(|_, cx| assert!(page.read(cx).main_scroll.offset().y < px(0.)));
}
#[gpui::test]
fn a_wheel_over_a_long_side_column_moves_only_that_column(cx: &mut TestAppContext) {
    let (page, visual) = page(cx, 60);
    wheel_over_the_side_column(visual);
    visual.update(|_, cx| {
        let page = page.read(cx);
        assert_eq!(page.main_scroll.offset().y, px(0.));
        assert!(page.side_scroll.offset().y < px(0.));
    });
}

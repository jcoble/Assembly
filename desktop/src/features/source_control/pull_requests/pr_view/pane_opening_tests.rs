//! Pull request pane tests for a page still opening: going back, a machine that does not answer, the tab it opens on,
//! and merging from the page.
use super::*;

#[gpui::test]
async fn going_back_from_a_page_still_opening_returns_to_the_list(cx: &mut TestAppContext) {
    let mut fake = fake(
        cx,
        json!([holder(LANE, "lane"), holder(FAR, "far")]),
        Source::Answers,
    );
    let window = cx.add_empty_window();
    let pane = pane_with_projects(&mut fake, window).await;
    window.update(|_, cx| pane.update(cx, |pane, cx| pane.select_pr(Some("o/r".into()), 7, cx)));
    window.update(|_, cx| {
        let pane = pane.read(cx);
        assert_eq!(pane.selected.as_ref().map(|wanted| wanted.number), Some(7));
        assert!(pane.choosing.is_some());
    });
    window.update(|_, cx| pane.update(cx, |pane, cx| pane.close_detail(cx)));
    window.update(|_, cx| {
        let pane = pane.read(cx);
        assert!(pane.selected.is_none() && pane.choosing.is_none() && pane.opening.is_none());
    });
    assert!(calls(&fake.seen, "resolve_source").is_empty());
}

#[gpui::test]
async fn a_machine_that_does_not_answer_keeps_the_page_and_refresh_asks_again(
    cx: &mut TestAppContext,
) {
    let mut fake = fake(cx, json!([holder(LANE, "lane")]), Source::RefusesFirst);
    let window = cx.add_empty_window();
    let pane = pane_with_projects(&mut fake, window).await;
    let woke = fake.changed.clone();
    let _observe = window.update(|_, cx| {
        cx.observe(&pane, move |_, _| {
            let _ = woke.unbounded_send(());
        })
    });
    window.update(|_, cx| pane.update(cx, |pane, cx| pane.select_pr(Some("o/r".into()), 7, cx)));
    until("the refusal", &mut fake.changes, &fake.changed, || {
        window.run_until_parked();
        window.update(|_, cx| pane.read(cx).failure.is_some())
    })
    .await;
    window.update(|_, cx| {
        let pane = pane.read(cx);
        assert_eq!(pane.failure.as_deref(), Some("The machine isn't there."));
        assert_eq!(pane.selected.as_ref().map(|wanted| wanted.number), Some(7));
        assert!(pane.opening.is_none() && pane.detail.is_none());
    });
    assert!(calls(&fake.seen, "pr").is_empty());

    window.update(|_, cx| pane.update(cx, |pane, cx| pane.retry_open(cx)));
    window.update(|_, cx| assert!(pane.read(cx).failure.is_none()));
    until("the second read", &mut fake.changes, &fake.changed, || {
        window.run_until_parked();
        !calls(&fake.seen, "pr").is_empty()
    })
    .await;
    assert_eq!(calls(&fake.seen, "resolve_source").len(), 2);
    assert_eq!(calls(&fake.seen, "pr")[0]["projectId"], LANE);
}

#[gpui::test]
async fn the_tab_picked_while_opening_is_the_tab_the_page_opens_on(cx: &mut TestAppContext) {
    let mut fake = fake(
        cx,
        json!([holder(LANE, "lane"), holder(FAR, "far")]),
        Source::Answers,
    );
    let window = cx.add_empty_window();
    let pane = pane_with_projects(&mut fake, window).await;
    window.update(|_, cx| {
        pane.update(cx, |pane, cx| {
            pane.select_pr(Some("o/r".into()), 7, cx);
            pane.show_tab(true, cx);
            pane.choose_project(FAR, cx);
        })
    });
    until("the read", &mut fake.changes, &fake.changed, || {
        window.run_until_parked();
        !calls(&fake.seen, "pr").is_empty()
    })
    .await;
    window.update(|_, cx| {
        assert!(
            pane.read(cx)
                .detail
                .as_ref()
                .expect("the page")
                .read(cx)
                .on_changes()
        );
    });
}

#[gpui::test]
async fn merging_on_the_page_takes_the_row_off_the_list_behind_it(cx: &mut TestAppContext) {
    let mut fake = fake(cx, json!([holder(LANE, "lane")]), Source::Answers);
    let window = cx.add_empty_window();
    let pane = pane_with_projects(&mut fake, window).await;
    window.update(|_, cx| {
        pane.update(cx, |pane, cx| {
            pane.active = true;
            pane.select_project(Some(LANE.into()), cx);
        })
    });
    // Picking a project makes the list this page came from.
    let queue = window.update(|_, cx| pane.read(cx).queue.clone());
    let woke = fake.changed.clone();
    let _observe = window.update(|_, cx| {
        cx.observe(&queue, move |_, _| {
            let _ = woke.unbounded_send(());
        })
    });
    until("the row", &mut fake.changes, &fake.changed, || {
        window.run_until_parked();
        window.update(|_, cx| queue.read(cx).page.items.len() == 1)
    })
    .await;
    window.update(|_, cx| pane.update(cx, |pane, cx| pane.select_pr(Some("o/r".into()), 7, cx)));
    until("the page", &mut fake.changes, &fake.changed, || {
        window.run_until_parked();
        !calls(&fake.seen, "pr").is_empty()
    })
    .await;
    window.update(|_, cx| {
        let detail = pane.read(cx).detail.clone().expect("the page");
        detail.update(cx, |_, cx| {
            cx.emit(PrMerged {
                repository: "O/R".into(),
                number: 7,
            })
        });
    });
    window.run_until_parked();
    window.update(|_, cx| assert!(queue.read(cx).page.items.is_empty()));
}

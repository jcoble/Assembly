//! Checkout scope tests for a saved checkout hint: it returns once a fresh list offers it, is dropped when it can't
//! stand, and falls back to the session folder when the read of it is refused or the checkout is no longer listed.
use super::*;

#[gpui::test]
async fn a_saved_checkout_comes_back_once_a_fresh_list_still_offers_it(cx: &mut TestAppContext) {
    let mut open = open(cx, linked()).await;
    let before = open.seen.lock().unwrap().checkouts.len();
    open.restore(saved(0));
    open.until("the other checkout's status", |pane, cx, seen| {
        let status = pane.update(cx, |pane, cx| pane.shown_status(cx));
        seen.checkouts.len() > before
            && status.is_some_and(|status| status.branch.as_deref() == Some("feature"))
    })
    .await;
    let seen = open.seen.lock().unwrap();
    let asked = seen.statuses.last().unwrap();
    assert_eq!(asked["checkoutPath"], LINKED);
    assert_eq!(asked["expectedCheckoutRoot"], OWN);
}
#[gpui::test]
async fn a_saved_checkout_no_longer_listed_is_dropped_and_said(cx: &mut TestAppContext) {
    let mut open = open(cx, offering(json!([]))).await;
    open.restore(saved(0));
    open.dropped_and_said().await;
}
#[gpui::test]
async fn a_saved_checkout_under_another_root_is_dropped_and_said(cx: &mut TestAppContext) {
    let mut open = open(cx, linked()).await;
    open.restore(CheckoutHint {
        expected_root: "/moved".into(),
        ..saved(0)
    });
    open.dropped_and_said().await;
}
#[gpui::test]
async fn a_saved_checkout_on_an_older_owner_is_dropped_and_said(cx: &mut TestAppContext) {
    let mut open = open(cx, None).await;
    open.restore(saved(0));
    open.dropped_and_said().await;
}
#[gpui::test]
async fn a_saved_checkout_from_a_replaced_owner_is_dropped_and_said(cx: &mut TestAppContext) {
    let mut open = open(cx, linked()).await;
    open.restore(saved(9));
    open.dropped_and_said().await;
}
#[gpui::test]
async fn a_saved_checkout_of_another_session_waits_for_that_session(cx: &mut TestAppContext) {
    let mut open = open(cx, linked()).await;
    open.restore(CheckoutHint {
        session: "s0".into(),
        ..saved(0)
    });
    let before = open.seen.lock().unwrap().checkouts.len();
    open.until("the next list", |_, _, seen| seen.checkouts.len() > before)
        .await;
    open.barrier().await;
    assert_eq!(open.selection(), None);
    assert!(open.hint().is_some(), "still there for its own session");
    assert!(!open.seen.lock().unwrap().statuses.iter().any(selects));
}
#[gpui::test]
async fn coming_back_to_the_session_brings_its_checkout_back(cx: &mut TestAppContext) {
    let mut open = open(cx, linked()).await;
    open.showing_linked().await;
    let select = |id: &str, cx: &mut VisualTestContext| {
        let id = id.to_string();
        cx.update(|_, cx| {
            SelectedSession::global(cx).update(cx, |selected, cx| {
                selected.commit_open(
                    SessionKey {
                        machine: "owner".into(),
                        id,
                    },
                    owner(0),
                    false,
                    cx,
                )
            })
        });
    };
    select("s2", open.cx);
    open.until("the next session's list", |_, _, seen| {
        seen.checkouts
            .last()
            .is_some_and(|args| args["session"] == "s2")
    })
    .await;
    assert_eq!(open.selection(), None);
    select("s1", open.cx);
    open.until("the checkout read again", |pane, cx, seen| {
        let shown = cx.update(|_, cx| pane.read(cx).selection(cx).is_some());
        shown
            && seen
                .statuses
                .last()
                .is_some_and(|args| args["session"] == "s1" && selects(args))
    })
    .await;
}
#[gpui::test]
async fn the_saved_layout_keeps_only_the_checkout_still_chosen(cx: &mut TestAppContext) {
    let mut open = open(cx, linked()).await;
    let pane = open.pane.clone();
    let bare = open
        .cx
        .update(|_, cx| serde_json::to_value(pane.read(cx).dump(cx)).unwrap());
    let empty = gpui_kit::component::dock::PanelState::new(super::super::SOURCE_CONTROL);
    assert_eq!(
        bare,
        serde_json::to_value(empty).unwrap(),
        "nothing new in a layout with no choice"
    );
    open.showing_linked().await;
    let key = WorkspaceKey {
        machine: "owner".into(),
        session_id: "s1".into(),
        checkout_root: OWN.into(),
    };
    pane.update(open.cx, |pane, _| pane.workspace = Some(key.clone()));
    let kept = open.cx.update(|_, cx| pane.read(cx).saved_view(&key));
    assert_eq!(
        kept["checkoutHint"],
        serde_json::to_value(saved(0)).unwrap()
    );
    let bare_again = open
        .cx
        .update(|_, cx| serde_json::to_value(pane.read(cx).dump(cx)).unwrap());
    assert_eq!(
        bare_again, bare,
        "the choice is saved with the workspace, not the device's layout"
    );
    open.pick(None);
    let kept = open.cx.update(|_, cx| pane.read(cx).saved_view(&key));
    assert_eq!(kept["checkoutHint"], Value::Null);
}
#[gpui::test]
async fn open_history_carries_the_checkout_being_read(cx: &mut TestAppContext) {
    let mut open = open(cx, linked()).await;
    let heard = Arc::new(Mutex::new(Vec::<OpenGitHistory>::new()));
    let keep = heard.clone();
    open.cx.update(|_, cx| {
        cx.on_action(move |intent: &OpenGitHistory, _| keep.lock().unwrap().push(intent.clone()));
    });
    let pane = open.pane.clone();
    pane.update_in(open.cx, |pane, window, cx| {
        pane.menu_action(MenuAction::OpenHistory, window, cx)
    });
    open.cx.run_until_parked();
    assert_eq!(
        heard
            .lock()
            .unwrap()
            .last()
            .map(|intent| intent.checkout.clone()),
        Some(None)
    );
    open.showing_linked().await;
    pane.update_in(open.cx, |pane, window, cx| {
        pane.menu_action(MenuAction::OpenHistory, window, cx)
    });
    open.cx.run_until_parked();
    let asked = heard
        .lock()
        .unwrap()
        .last()
        .and_then(|intent| intent.checkout.clone())
        .expect("the checkout");
    assert_eq!(asked.path, LINKED);
    assert_eq!(asked.expected_root, OWN);
}

#[gpui::test]
async fn a_refused_checkout_goes_back_to_the_session_folder_and_says_so(cx: &mut TestAppContext) {
    let mut open = open(cx, linked()).await;
    open.seen.lock().unwrap().refused = Some("checkout_unavailable");
    open.pick(Some(LINKED));
    open.until("back on its own folder", |pane, cx, seen| {
        let status = pane.update(cx, |pane, cx| pane.shown_status(cx));
        seen.statuses.iter().any(selects)
            && status.is_some_and(|status| status.branch.as_deref() == Some("main"))
    })
    .await;
    assert_eq!(open.selection(), None);
    assert!(
        !open
            .seen
            .lock()
            .unwrap()
            .statuses
            .last()
            .is_some_and(selects)
    );
    let said = open.pane.clone();
    open.cx.update(|_, cx| {
        assert!(matches!(&said.read(cx).result, Some(PaneResult::Failed(failure)) if failure.sentence.contains("went back to the session folder")));
    });
}
#[gpui::test]
async fn a_checkout_no_longer_listed_goes_back_to_the_session_folder(cx: &mut TestAppContext) {
    let mut open = open(cx, linked()).await;
    open.showing_linked().await;
    open.seen.lock().unwrap().offered = offering(json!([]));
    let pane = open.pane.clone();
    pane.update(open.cx, |pane, cx| {
        pane.read.update(cx, |read, cx| read.refresh(cx))
    });
    open.until("back on its own folder", |pane, cx, _| {
        cx.update(|_, cx| pane.read(cx).selection(cx).is_none())
    })
    .await;
    open.until("its own status", |pane, cx, _| {
        let status = pane.update(cx, |pane, cx| pane.shown_status(cx));
        status.is_some_and(|status| status.branch.as_deref() == Some("main"))
    })
    .await;
    assert!(
        !open
            .seen
            .lock()
            .unwrap()
            .statuses
            .last()
            .is_some_and(selects)
    );
}

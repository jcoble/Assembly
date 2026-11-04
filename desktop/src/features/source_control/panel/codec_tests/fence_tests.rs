//! The conditions a saved place has to meet, one at a time: every other condition holds, so only this one can refuse it.
use super::{Bench, ONE, SavedView, TWO, at, bench, hint, in_session, key, linked};
use crate::core::socket::client::SocketClient;
use crate::core::state::session_read::Shown;
use crate::core::state::workspace_panes::WorkspaceKey;
use gpui::TestAppContext;
use serde_json::{Value, json};

impl Bench<'_> {
    /// Everything asked so far has reached the backend: frames arrive in order, so one answered round trip covers them.
    /// A check that something was not asked needs this, since parking the app doesn't wait for the socket.
    pub(super) async fn delivered(&mut self) {
        self.cx.run_until_parked();
        let call = self.cx.update(|_, cx| {
            SocketClient::try_global(cx)
                .expect("the socket is running")
                .read(cx)
                .call::<Value>("git", "delivered", json!({}), None)
        });
        let _ = call.await;
    }
}
#[gpui::test]
async fn a_linked_status_naming_another_root_leaves_the_place_waiting(cx: &mut TestAppContext) {
    let mut bench = bench(cx, linked()).await;
    bench.seen.lock().unwrap().alternate_root = Some("/elsewhere".into());
    bench.show();
    bench.answered(1).await;
    bench.bind(
        Some(key(ONE)),
        SavedView {
            checkout_hint: Some(hint()),
            ..at(40.)
        },
    );
    bench
        .until("the linked checkout's answer", |pane, cx, _| {
            let read = pane.read.read(cx);
            read.checkout().is_some() && matches!(read.shown(), Shown::Read(_))
        })
        .await;
    assert_eq!(bench.spot(), (Some(40.), 0.));
}
#[gpui::test]
async fn a_checkout_expecting_another_root_does_not_prove_this_workspace(cx: &mut TestAppContext) {
    let mut bench = bench(cx, linked()).await;
    bench.show();
    bench.answered(1).await;
    bench.bind(Some(key(TWO)), at(40.));
    bench.answered(2).await;
    let pane = bench.pane.clone();
    pane.update(bench.cx, |pane, cx| {
        pane.read.update(cx, |read, cx| {
            read.set_checkout(Some(hint().selector()), cx)
        })
    });
    bench.answered(3).await;
    assert_eq!(bench.selection(), Some(hint().selector()));
    assert_eq!(bench.spot(), (Some(40.), 0.));
}
#[gpui::test]
async fn a_status_for_another_session_waits_until_that_session_is_read(cx: &mut TestAppContext) {
    let mut bench = bench(cx, json!([])).await;
    bench.show();
    bench.answered(1).await;
    bench.bind(Some(in_session("s2", ONE)), at(40.));
    bench.answered(2).await;
    assert_eq!(bench.spot(), (Some(40.), 0.));
    bench.select("s2");
    bench.answered(3).await;
    assert_eq!(bench.spot(), (None, -40.));
}
#[gpui::test]
async fn a_status_from_another_machine_leaves_the_place_waiting(cx: &mut TestAppContext) {
    let mut bench = bench(cx, json!([])).await;
    bench.show();
    bench.answered(1).await;
    bench.bind(
        Some(WorkspaceKey {
            machine: "other".into(),
            ..key(ONE)
        }),
        at(40.),
    );
    bench.answered(2).await;
    assert_eq!(bench.spot(), (Some(40.), 0.));
}
#[gpui::test]
async fn a_checkout_hint_from_another_workspace_stays_out_of_the_save(cx: &mut TestAppContext) {
    let mut bench = bench(cx, json!([])).await;
    bench.bind(Some(key(TWO)), SavedView::default());
    let pane = bench.pane.clone();
    let saved = pane.update(bench.cx, |pane, _| {
        pane.checkout_hint = Some(hint());
        pane.saved_view(&key(TWO))
    });
    assert_eq!(saved["checkoutHint"], Value::Null);
    assert_eq!(
        SavedView::parse(&saved, &key(TWO)).map(|view| view.checkout_hint),
        Ok(None)
    );
}
#[gpui::test]
async fn a_list_that_already_landed_is_gone_as_soon_as_the_workspace_changes(
    cx: &mut TestAppContext,
) {
    let mut bench = bench(cx, linked()).await;
    bench.show();
    bench
        .until("the list", |pane, _, _| pane.checkouts.is_some())
        .await;
    let pane = bench.pane.clone();
    pane.update(bench.cx, |pane, cx| {
        pane.bind_workspace(Some(key(TWO)), SavedView::default(), cx);
        assert!(pane.checkouts.is_none());
    });
}
#[gpui::test]
async fn a_list_on_its_way_is_let_go_by_the_bind_itself(cx: &mut TestAppContext) {
    let mut bench = bench(cx, linked()).await;
    bench.seen.lock().unwrap().hold_list = true;
    bench.show();
    bench
        .until("the list is on its way", |pane, _, seen| {
            seen.lists == 1 && pane.checkouts_read.is_some()
        })
        .await;
    let pane = bench.pane.clone();
    pane.update(bench.cx, |pane, cx| {
        pane.bind_workspace(Some(key(TWO)), SavedView::default(), cx);
        assert!(pane.checkouts_read.is_none() && pane.checkouts_for.is_none());
    });
}
/// The bind drops the task it knows about. This one is held on to, and answers for the same session and source, so only
/// the workspace it was asked under can refuse it.
#[gpui::test]
async fn a_list_answering_for_the_last_workspace_of_the_same_session_is_refused(
    cx: &mut TestAppContext,
) {
    let mut bench = bench(cx, linked()).await;
    bench.show();
    bench
        .until("the list", |pane, _, _| pane.checkouts.is_some())
        .await;
    let pane = bench.pane.clone();
    let (kept, asked) = pane.update(bench.cx, |pane, cx| {
        let asked = pane.checkouts_for.clone();
        pane.read_checkouts(cx);
        (pane.checkouts_read.take(), asked)
    });
    bench.bind(Some(key(ONE)), SavedView::default());
    pane.update(bench.cx, |pane, _| {
        pane.checkouts_for = asked;
        pane.checkouts_read = None;
    });
    kept.expect("the list is on its way").await;
    pane.update(bench.cx, |pane, _| assert!(pane.checkouts.is_none()));
}
/// Stopping first is what keeps clearing the selector from starting a read of its own under the last workspace.
#[gpui::test]
async fn leaving_a_linked_checkout_for_the_next_workspace_asks_once(cx: &mut TestAppContext) {
    let mut bench = bench(cx, linked()).await;
    bench.show();
    bench.answered(1).await;
    bench.bind(
        Some(key(ONE)),
        SavedView {
            checkout_hint: Some(hint()),
            ..SavedView::default()
        },
    );
    bench
        .until("the linked checkout's answer", |pane, cx, _| {
            let read = pane.read.read(cx);
            read.checkout().is_some() && matches!(read.shown(), Shown::Read(_))
        })
        .await;
    let pane = bench.pane.clone();
    let answers = pane.update(bench.cx, |pane, cx| pane.read.read(cx).answers());
    let before = bench.seen.lock().unwrap().statuses.len();
    bench.bind(Some(key(TWO)), SavedView::default());
    bench.answered(answers + 1).await;
    let seen = bench.seen.lock().unwrap();
    assert_eq!(seen.statuses.len(), before + 1);
    assert_eq!(seen.statuses[before].get("checkoutPath"), None);
}

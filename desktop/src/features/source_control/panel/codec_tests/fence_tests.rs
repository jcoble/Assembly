//! The conditions a saved place has to meet, one at a time: every other condition holds, so only this one can refuse it.
use super::{Bench, ONE, at, bench, in_session, key};
use crate::core::socket::client::SocketClient;
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

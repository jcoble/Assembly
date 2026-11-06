//! Session open tests for native cards, native stop and the mark-read source of read-only history.
use crate::core::engine::SessionKey;
use crate::core::intents::session_open::SessionOpenTarget;
use crate::core::state::selected_session::SelectedSession;
use gpui::TestAppContext;

#[gpui::test]
fn a_native_card_open_keeps_the_actual_parent_item_seq_and_payload_correlation(
    cx: &mut TestAppContext,
) {
    use crate::core::socket::row_source::ExpectedRowSource;
    use crate::features::session::rows::row::Row;
    let (pane, _, cx) = super::tests::open(cx);
    pane.update_in(cx,|pane,_,_| {
        pane.session=Some("assembly-parent".into());
        pane.row_source=Some(ExpectedRowSource{machine:"actual-engine".into(),generation:9,route_machine:Some("captured".into())});
        let row:Row=serde_json::from_value(serde_json::json!({"sessionId":"assembly-parent","seq":7,"itemId":"subagent:actual-acp","runId":null,"kind":"subagent",
            "payload":{"sessionId":"assembly-parent","title":"Native","state":"idle","canStop":false},"createdAt":1})).unwrap();
        pane.view.apply(&row);
        let item_key=pane.view.items[0].key.clone();
        let request=pane.native_open_request(&item_key,"assembly-parent").unwrap();
        assert_eq!(request.key,SessionKey{machine:"actual-engine".into(),id:"assembly-parent".into()});
        assert!(matches!(request.target,SessionOpenTarget::Native{parent,item_id,seq:7,payload_session_id} if parent==request.key&&item_id=="subagent:actual-acp"&&payload_session_id=="assembly-parent"));
        assert!(pane.native_open_request(&item_key,"stale-payload").is_err());
    });
}

#[gpui::test]
fn readonly_history_does_not_issue_an_unqualified_mark_read_write(cx: &mut TestAppContext) {
    let (pane, client, cx) = super::tests::open(cx);
    pane.update_in(cx, |pane, _, cx| {
        pane.session = Some("history".into());
        pane.view.newest = 9;
        pane.read_only = true;
        pane.tell_read(false, cx);
    });
    cx.run_until_parked();
    assert!(client.reads().is_empty());
    pane.update_in(cx, |pane, _, cx| {
        pane.read_only = false;
        pane.tell_read(false, cx);
    });
    cx.run_until_parked();
    assert_eq!(
        client.reads(),
        vec![serde_json::json!({"session":"history","seq":9})]
    );
}

#[gpui::test]
fn native_stop_sends_parent_and_the_exact_persisted_provider_child_suffix(cx: &mut TestAppContext) {
    use crate::core::socket::row_source::ExpectedRowSource;
    use crate::features::session::{rows::row::Row, view::session_action::SessionAction};
    let (pane, client, cx) = super::tests::open(cx);
    client.queue_answer(Ok(serde_json::Value::Null));
    pane.update_in(cx,|pane,window,cx|{
        pane.session=Some("parent".into());pane.row_source=Some(ExpectedRowSource{machine:"engine".into(),generation:0,route_machine:None});
        pane._rows=Some(gpui_kit::Subscription::new(||{}));
        let mut row:Row=serde_json::from_value(serde_json::json!({"sessionId":"parent","seq":7,"itemId":"subagent:actual-provider-child","runId":null,"kind":"subagent",
            "payload":{"sessionId":"parent","title":"Native","state":"running","canStop":true},"createdAt":1})).unwrap();
        pane.view.apply(&row);let key=pane.view.items[0].key.clone();
        pane.on_action(SessionAction::StopNative{key:key.clone(),payload_session_id:"parent".into()},window,cx);
        pane.on_action(SessionAction::StopNative{key:key.clone(),payload_session_id:"wrong-parent".into()},window,cx);
        row.seq=8;row.payload["helper"]=serde_json::json!(true);pane.view.apply(&row);
        pane.on_action(SessionAction::StopNative{key,payload_session_id:"parent".into()},window,cx);
    });
    cx.run_until_parked();
    assert_eq!(
        client.calls(),
        vec![(
            "send/stopTask".into(),
            serde_json::json!({"session":"parent","task":"actual-provider-child","expectedSource":{"machine":"engine","generation":0,"routeMachine":null}})
        )]
    );
}

#[gpui::test]
fn stale_nonstoppable_malformed_and_refused_native_cards_send_nothing(cx: &mut TestAppContext) {
    use crate::core::socket::row_source::ExpectedRowSource;
    use crate::features::session::{
        client::session_client::RowUpdate, rows::row::Row, view::session_action::SessionAction,
    };
    let (pane, client, cx) = super::tests::open(cx);
    pane.update_in(cx,|pane,window,cx|{
        pane.session=Some("parent".into());pane.row_source=Some(ExpectedRowSource{machine:"engine".into(),generation:0,route_machine:None});
        pane._rows=Some(gpui_kit::Subscription::new(||{}));
        let mut row:Row=serde_json::from_value(serde_json::json!({"sessionId":"parent","seq":7,"itemId":"subagent:child","runId":null,"kind":"subagent",
            "payload":{"sessionId":"parent","title":"Native","state":"running","canStop":false},"createdAt":1})).unwrap();
        pane.view.apply(&row);let key=pane.view.items[0].key.clone();
        pane.on_action(SessionAction::StopNative{key:key.clone(),payload_session_id:"parent".into()},window,cx);
        row.seq=8;row.payload["canStop"]=serde_json::json!(true);row.payload["state"]=serde_json::json!("idle");pane.view.apply(&row);
        pane.on_action(SessionAction::StopNative{key:key.clone(),payload_session_id:"parent".into()},window,cx);
        row.seq=9;row.item_id=Some("malformed".into());row.payload["state"]=serde_json::json!("running");pane.view.apply(&row);
        let malformed=pane.view.items.last().unwrap().key.clone();
        pane.on_action(SessionAction::StopNative{key:malformed,payload_session_id:"parent".into()},window,cx);
        pane.on_rows(RowUpdate::Refused{code:"source_changed".into(),message:"Changed".into()},cx);
        pane.on_action(SessionAction::StopNative{key,payload_session_id:"parent".into()},window,cx);
    });
    cx.run_until_parked();
    assert!(client.calls().is_empty());
}

#[gpui::test]
fn a_qualified_readonly_journal_marks_read_with_its_exact_descriptor_source(
    cx: &mut TestAppContext,
) {
    use crate::core::socket::row_source::ExpectedRowSource;
    let (pane, client, cx) = super::tests::open(cx);
    let source = ExpectedRowSource {
        machine: "actual-owner".into(),
        generation: 41,
        route_machine: Some("saved-route".into()),
    };
    pane.update_in(cx, |pane, _, cx| {
        pane.session = Some("history".into());
        pane.view.newest = 9;
        pane.read_only = true;
        pane.row_source = Some(source.clone());
        pane.tell_read(false, cx);
    });
    cx.run_until_parked();
    assert_eq!(
        client.reads(),
        vec![serde_json::json!({"session":"history","seq":9,"expectedSource":source})]
    );
}

#[gpui::test]
fn a_mark_read_source_refusal_is_visible_and_never_retried_without_its_source(
    cx: &mut TestAppContext,
) {
    use crate::{
        core::socket::row_source::ExpectedRowSource,
        features::session::client::session_client::CallError,
    };
    let (pane, client, cx) = super::tests::open(cx);
    client.queue_read_answer(Err(CallError::Refused {
        code: "source_changed".into(),
        message: "captured owner replaced".into(),
    }));
    pane.update_in(cx, |pane, _, cx| {
        pane.session = Some("history".into());
        pane.view.newest = 9;
        pane.read_only = true;
        pane.row_source = Some(ExpectedRowSource {
            machine: "owner".into(),
            generation: 0,
            route_machine: None,
        });
        pane.tell_read(false, cx);
    });
    cx.run_until_parked();
    pane.update_in(cx, |pane, _, cx| {
        assert!(pane.status.contains("captured owner replaced"));
        assert_eq!(pane.session.as_deref(), Some("history"));
        pane.tell_read(false, cx);
    });
    cx.run_until_parked();
    assert_eq!(client.reads().len(), 1);
    assert!(client.reads()[0].get("expectedSource").is_some());
}

#[gpui::test]
fn selected_source_is_only_the_prepared_descriptor_tuple_and_fixture_selection_clears_it(
    cx: &mut TestAppContext,
) {
    use crate::core::socket::row_source::ExpectedRowSource;
    cx.update(|cx| {
        SelectedSession::init(cx);
        let selected = SelectedSession::global(cx);
        let source = ExpectedRowSource {
            machine: "actual-owner".into(),
            generation: 29,
            route_machine: Some("captured-route".into()),
        };
        selected.update(cx, |selected, cx| {
            selected.commit_open(
                SessionKey {
                    machine: "actual-owner".into(),
                    id: "session".into(),
                },
                source.clone(),
                true,
                cx,
            )
        });
        assert_eq!(selected.read(cx).source(), Some(&source));
        assert!(selected.read(cx).read_only());
        selected.update(cx, |selected, cx| {
            selected.select(Some("session".into()), cx)
        });
        assert!(selected.read(cx).source().is_none());
        assert!(selected.read(cx).key().is_none());
    });
}

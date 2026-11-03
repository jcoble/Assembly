use super::GitOperation;
use crate::core::socket::{
    client::SocketClient,
    endpoint::Endpoint,
    fake_backend::{FakeBackend, Reply, hello},
    test_wait::until,
};
use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use gpui_kit::AppContext as _;
use serde_json::{Value, json};
use std::sync::{Arc, Mutex};
// Pins: g9#10
#[gpui::test]
async fn an_operation_subscribes_before_start_and_terminal_push_is_not_downgraded(
    cx: &mut TestAppContext,
) {
    let seen = Arc::new(Mutex::new(Vec::<Value>::new()));
    let observed = seen.clone();
    let (changed, mut changes) = unbounded();
    let heard = changed.clone();
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        let mut seen = observed.lock().unwrap();
        seen.push(frame.clone());
        let replies = if frame["t"] == "sub" {
            vec![Reply::Frame(
                json!({"t":"state","sub":frame["id"],"items":[]}),
            )]
        } else if frame["t"] == "call" && frame["method"] == "operation_host" {
            vec![Reply::Frame(
                json!({"t":"result","id":frame["id"],"result":{"instanceId":"instance","machine":"engine"}}),
            )]
        } else if frame["t"] == "call" && frame["method"] == "start" {
            let subscription = seen
                .iter()
                .find(|item| item["t"] == "sub")
                .expect("subscribe before start");
            let id = frame["args"]["operationId"].clone();
            let status = |version, state| json!({"operationId":id,"instanceId":"instance","machine":"engine","version":version,"state":state,"result":{"made":true},"error":null});
            vec![
                Reply::Frame(json!({"t":"result","id":frame["id"],"result":status(1,"accepted")})),
                Reply::Frame(
                    json!({"t":"change","sub":subscription["id"],"key":subscription["key"],"item":status(3,"succeeded")}),
                ),
                Reply::Frame(
                    json!({"t":"change","sub":subscription["id"],"key":subscription["key"],"item":status(2,"running")}),
                ),
            ]
        } else {
            vec![]
        };
        drop(seen);
        let _ = heard.unbounded_send(());
        replies
    });
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        SocketClient::start(
            Endpoint {
                port: backend.port,
                token: "secret".into(),
            },
            cx,
        );
    });
    let operation = cx.new(|_| GitOperation::default());
    operation.update(cx,|operation,cx|operation.start("createProject",None,json!({"machine":"local","parentPath":"/owned","name":"fixture","kind":"empty","source":null}),cx));
    let tell = changed.clone();
    let _heard = cx.update(|cx| {
        cx.observe(&operation, move |_, _| {
            let _ = tell.unbounded_send(());
        })
    });
    until("terminal push", &mut changes, &changed, || {
        cx.run_until_parked();
        cx.update(|cx| {
            operation
                .read(cx)
                .status
                .as_ref()
                .is_some_and(|status| status.state == "succeeded")
        })
    })
    .await;
    assert_eq!(
        cx.update(|cx| operation.read(cx).status.as_ref().unwrap().version),
        3
    );
    until(
        "owned subscription released",
        &mut changes,
        &changed,
        || {
            cx.run_until_parked();
            seen.lock()
                .unwrap()
                .iter()
                .any(|frame| frame["t"] == "unsub")
        },
    )
    .await;
    assert_eq!(
        seen.lock()
            .unwrap()
            .iter()
            .filter(|frame| frame["method"] == "start")
            .count(),
        1
    );
    assert!(
        !seen
            .lock()
            .unwrap()
            .iter()
            .any(|frame| frame["method"] == "cancel")
    );
}

#[gpui::test]
async fn a_definite_start_refusal_stops_watching_but_dispatched_uncertainty_never_restarts(
    cx: &mut TestAppContext,
) {
    let seen = Arc::new(Mutex::new(Vec::<Value>::new()));
    let observed = seen.clone();
    let (changed, mut changes) = unbounded();
    let heard = changed.clone();
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        observed.lock().unwrap().push(frame.clone());
        let _ = heard.unbounded_send(());
        if frame["t"] == "sub" {
            return vec![Reply::Frame(
                json!({"t":"state","sub":frame["id"],"items":[]}),
            )];
        }
        if frame["t"] != "call" {
            return vec![];
        }
        if frame["method"] == "operation_host" {
            return vec![Reply::Frame(
                json!({"t":"result","id":frame["id"],"result":{"instanceId":"owned","machine":"machine"}}),
            )];
        }
        if frame["method"] == "start" {
            let code = if frame["args"]["session"] == "refused" {
                "bad_args"
            } else {
                "uncertain"
            };
            return vec![Reply::Frame(
                json!({"t":"error","id":frame["id"],"code":code,"message":"actual backend result","machine":"machine"}),
            )];
        }
        vec![]
    });
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        SocketClient::start(
            Endpoint {
                port: backend.port,
                token: "secret".into(),
            },
            cx,
        );
    });
    let refused = cx.new(|_| GitOperation::default());
    let unknown = cx.new(|_| GitOperation::default());
    let sent = changed.clone();
    let _r = cx.update(|cx| {
        cx.observe(&refused, move |_, _| {
            let _ = sent.unbounded_send(());
        })
    });
    let sent = changed.clone();
    let _u = cx.update(|cx| {
        cx.observe(&unknown, move |_, _| {
            let _ = sent.unbounded_send(());
        })
    });
    refused.update(cx, |operation, cx| {
        operation.start(
            "push",
            Some("refused".into()),
            json!({"expectedBranch":"feature"}),
            cx,
        )
    });
    until("definite refusal", &mut changes, &changed, || {
        cx.run_until_parked();
        cx.update(|cx| refused.read(cx).error.is_some())
    })
    .await;
    cx.update(|cx| {
        assert!(!refused.read(cx).busy);
        assert!(!refused.read(cx).unconfirmed);
        assert_eq!(
            refused.read(cx).error.as_deref(),
            Some("actual backend result")
        );
    });
    unknown.update(cx, |operation, cx| {
        operation.start(
            "push",
            Some("unknown".into()),
            json!({"expectedBranch":"feature"}),
            cx,
        )
    });
    until("typed uncertainty", &mut changes, &changed, || {
        cx.run_until_parked();
        cx.update(|cx| unknown.read(cx).unconfirmed)
    })
    .await;
    cx.update(|cx| assert!(!unknown.read(cx).busy, "Unknown completion retains reconciliation without claiming active observation."));
    unknown.update(cx, |operation, cx| {
        operation.start(
            "push",
            Some("unknown".into()),
            json!({"expectedBranch":"feature"}),
            cx,
        )
    });
    cx.run_until_parked();
    assert_eq!(
        seen.lock()
            .unwrap()
            .iter()
            .filter(|frame| frame["method"] == "start" && frame["args"]["session"] == "unknown")
            .count(),
        1
    );
}

#[gpui::test]
async fn refreshing_an_uncertain_operation_reads_the_captured_id_without_starting_it_again(
    cx: &mut TestAppContext,
) {
    let seen = Arc::new(Mutex::new(Vec::<Value>::new()));
    let observed = seen.clone();
    let (changed, mut changes) = unbounded();
    let heard = changed.clone();
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        observed.lock().unwrap().push(frame.clone());
        let _ = heard.unbounded_send(());
        if frame["t"] == "sub" {
            return vec![Reply::Frame(
                json!({"t":"state","sub":frame["id"],"items":[]}),
            )];
        }
        if frame["t"] != "call" {
            return vec![];
        }
        let result = match frame["method"].as_str() {
            Some("operation_host") => json!({"instanceId":"owned","machine":"machine"}),
            Some("start") | Some("operation") => {
                let known = frame["method"] == "operation";
                json!({"operationId":frame["args"]["operationId"],"instanceId":"owned","machine":"machine","version":if known{3}else{2},"state":if known{"succeeded"}else{"uncertain"},"result":{},"error":null})
            }
            _ => return vec![],
        };
        vec![Reply::Frame(
            json!({"t":"result","id":frame["id"],"result":result}),
        )]
    });
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        SocketClient::start(
            Endpoint {
                port: backend.port,
                token: "secret".into(),
            },
            cx,
        );
    });
    let operation = cx.new(|_| GitOperation::default());
    let sent = changed.clone();
    let _heard = cx.update(|cx| {
        cx.observe(&operation, move |_, _| {
            let _ = sent.unbounded_send(());
        })
    });
    operation.update(cx, |operation, cx| {
        operation.start(
            "push",
            Some("session".into()),
            json!({"expectedBranch":"feature"}),
            cx,
        )
    });
    until("uncertain retained result", &mut changes, &changed, || {
        cx.run_until_parked();
        cx.update(|cx| operation.read(cx).unconfirmed)
    })
    .await;
    operation.update(cx, |operation, cx| operation.reconcile(cx));
    until("confirmed retained result", &mut changes, &changed, || {
        cx.run_until_parked();
        cx.update(|cx| {
            operation
                .read(cx)
                .status
                .as_ref()
                .is_some_and(|status| status.state == "succeeded")
        })
    })
    .await;
    cx.update(|cx| {
        assert!(!operation.read(cx).busy);
        assert!(!operation.read(cx).unconfirmed);
    });
    let seen = seen.lock().unwrap();
    let start = seen
        .iter()
        .find(|frame| frame["method"] == "start")
        .unwrap();
    let read = seen
        .iter()
        .find(|frame| frame["method"] == "operation")
        .unwrap();
    assert_eq!(start["args"]["operationId"], read["args"]["operationId"]);
    assert_eq!(read["args"]["instanceId"], "owned");
    assert_eq!(
        seen.iter()
            .filter(|frame| frame["method"] == "start")
            .count(),
        1
    );
}

#[gpui::test]
async fn explicit_cancel_targets_the_captured_operation_and_waits_for_its_final_outcome(
    cx: &mut TestAppContext,
) {
    let seen = Arc::new(Mutex::new(Vec::<Value>::new()));
    let observed = seen.clone();
    let (changed, mut changes) = unbounded();
    let heard = changed.clone();
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        observed.lock().unwrap().push(frame.clone());
        let _ = heard.unbounded_send(());
        if frame["t"] == "sub" {
            return vec![Reply::Frame(
                json!({"t":"state","sub":frame["id"],"items":[]}),
            )];
        }
        if frame["t"] != "call" {
            return vec![];
        }
        let result = match frame["method"].as_str() {
            Some("operation_host") => {
                json!({"instanceId":"captured-instance","machine":"actual-machine"})
            }
            Some("start") => {
                json!({"operationId":frame["args"]["operationId"],"instanceId":"captured-instance","machine":"actual-machine","version":1,"state":"running","result":null,"error":null})
            }
            Some("cancel") => json!({"requested":true}),
            Some("operation") => {
                json!({"operationId":frame["args"]["operationId"],"instanceId":"captured-instance","machine":"actual-machine","version":2,"state":"cancelled","result":null,"error":null})
            }
            _ => return vec![],
        };
        vec![Reply::Frame(
            json!({"t":"result","id":frame["id"],"result":result}),
        )]
    });
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        SocketClient::start(
            Endpoint {
                port: backend.port,
                token: "secret".into(),
            },
            cx,
        );
    });
    let operation = cx.new(|_| GitOperation::default());
    let sent = changed.clone();
    let _heard = cx.update(|cx| {
        cx.observe(&operation, move |_, _| {
            let _ = sent.unbounded_send(());
        })
    });
    operation.update(cx, |operation, cx| {
        operation.start(
            "push",
            Some("owning-session".into()),
            json!({"expectedBranch":"feature"}),
            cx,
        )
    });
    until("running push", &mut changes, &changed, || {
        cx.run_until_parked();
        cx.update(|cx| {
            operation
                .read(cx)
                .status
                .as_ref()
                .is_some_and(|status| status.state == "running")
        })
    })
    .await;
    operation.update(cx, |operation, cx| {
        operation.cancel(cx);
        operation.cancel(cx);
    });
    until("cancel acknowledged", &mut changes, &changed, || {
        cx.run_until_parked();
        cx.update(|cx| {
            operation.read(cx).error.as_deref()
                == Some("Cancellation requested; waiting for the actual outcome.")
        })
    })
    .await;
    cx.update(|cx| {
        assert!(operation.read(cx).busy);
        assert_eq!(operation.read(cx).status.as_ref().unwrap().state, "running");
    });
    operation.update(cx, |operation, cx| operation.reconcile(cx));
    until("actual cancellation", &mut changes, &changed, || {
        cx.run_until_parked();
        cx.update(|cx| {
            operation
                .read(cx)
                .status
                .as_ref()
                .is_some_and(|status| status.state == "cancelled")
        })
    })
    .await;
    cx.update(|cx| assert!(!operation.read(cx).busy));
    until(
        "reconciled subscription released",
        &mut changes,
        &changed,
        || {
            cx.run_until_parked();
            seen.lock()
                .unwrap()
                .iter()
                .any(|frame| frame["t"] == "unsub")
        },
    )
    .await;
    let seen = seen.lock().unwrap();
    let start = seen
        .iter()
        .find(|frame| frame["method"] == "start")
        .unwrap();
    let cancel = seen
        .iter()
        .find(|frame| frame["method"] == "cancel")
        .unwrap();
    assert_eq!(cancel["args"]["operationId"], start["args"]["operationId"]);
    assert_eq!(cancel["args"]["instanceId"], "captured-instance");
    assert_eq!(cancel["args"]["session"], "owning-session");
    assert_eq!(cancel["args"]["action"], "push");
    assert_eq!(
        seen.iter()
            .filter(|frame| frame["method"] == "start")
            .count(),
        1
    );
    assert_eq!(
        seen.iter()
            .filter(|frame| frame["method"] == "cancel")
            .count(),
        1
    );
}

#[gpui::test]
async fn a_stale_terminal_update_does_not_end_the_current_operation_watch(cx: &mut TestAppContext) {
    let seen = Arc::new(Mutex::new(Vec::<Value>::new()));
    let observed = seen.clone();
    let (changed, mut changes) = unbounded();
    let heard = changed.clone();
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        let mut seen = observed.lock().unwrap();
        seen.push(frame.clone());
        let _ = heard.unbounded_send(());
        if frame["t"] == "sub" {
            return vec![Reply::Frame(
                json!({"t":"state","sub":frame["id"],"items":[]}),
            )];
        }
        if frame["t"] != "call" {
            return vec![];
        }
        if frame["method"] == "operation_host" {
            return vec![Reply::Frame(
                json!({"t":"result","id":frame["id"],"result":{"instanceId":"owned","machine":"machine"}}),
            )];
        }
        if frame["method"] == "start" {
            let sub = seen.iter().find(|frame| frame["t"] == "sub").unwrap();
            let status = |version, state| json!({"operationId":frame["args"]["operationId"],"instanceId":"owned","machine":"machine","version":version,"state":state,"result":null,"error":null});
            return vec![
                Reply::Frame(json!({"t":"result","id":frame["id"],"result":status(3,"running")})),
                Reply::Frame(
                    json!({"t":"change","sub":sub["id"],"key":sub["key"],"item":status(2,"failed")}),
                ),
                Reply::Frame(
                    json!({"t":"change","sub":sub["id"],"key":sub["key"],"item":status(4,"succeeded")}),
                ),
            ];
        }
        vec![]
    });
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        SocketClient::start(
            Endpoint {
                port: backend.port,
                token: "secret".into(),
            },
            cx,
        );
    });
    let operation = cx.new(|_| GitOperation::default());
    let sent = changed.clone();
    let _heard = cx.update(|cx| {
        cx.observe(&operation, move |_, _| {
            let _ = sent.unbounded_send(());
        })
    });
    operation.update(cx, |operation, cx| {
        operation.start(
            "push",
            Some("actual-session".into()),
            json!({"expectedBranch":"feature"}),
            cx,
        )
    });
    until("newer terminal result", &mut changes, &changed, || {
        cx.run_until_parked();
        cx.update(|cx| {
            operation
                .read(cx)
                .status
                .as_ref()
                .is_some_and(|status| status.version == 4 && status.state == "succeeded")
        })
    })
    .await;
    assert_eq!(
        seen.lock()
            .unwrap()
            .iter()
            .filter(|frame| frame["method"] == "start")
            .count(),
        1
    );
}

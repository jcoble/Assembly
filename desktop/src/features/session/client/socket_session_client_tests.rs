use gpui::TestAppContext;
use serde_json::{Value, json};

use super::{CallError, CallFailure, ErrorCode, SessionClient, SocketSessionClient, call_error};
use crate::core::socket::client::SocketClient;
use crate::core::socket::endpoint::Endpoint;
use crate::core::socket::fake_backend::{FakeBackend, Reply, hello, unused_port};

/// Welcomes each socket and answers every call with an empty result.
fn backend() -> FakeBackend {
    FakeBackend::start(|frame| {
        hello(frame).unwrap_or_else(|| {
            vec![Reply::Frame(
                json!({"t": "result", "id": frame["id"], "result": {}}),
            )]
        })
    })
}

fn endpoint(port: u16) -> Endpoint {
    Endpoint {
        port,
        token: "secret".into(),
    }
}

async fn call(feature: &str, method: &str, cx: &mut TestAppContext) -> Result<Value, CallError> {
    cx.update(|cx| SocketSessionClient.call(feature, method, json!({}), None, cx))
        .await
}

// The backend serves attachments/*, rows/page and rows/tool only on /socket/bulk, and every other call only on /socket.
#[gpui::test]
async fn the_large_calls_go_on_the_bulk_socket_and_the_rest_on_the_main_one(
    cx: &mut TestAppContext,
) {
    let backend = backend();
    cx.executor().allow_parking();
    cx.update(|cx| {
        SocketClient::start(endpoint(backend.port), cx);
        SocketClient::start_bulk(endpoint(backend.port), cx);
    });
    let calls = [
        ("attachments", "begin", "/socket/bulk"),
        ("attachments", "chunk", "/socket/bulk"),
        ("attachments", "commit", "/socket/bulk"),
        ("attachments", "drop", "/socket/bulk"),
        ("attachments", "read", "/socket/bulk"),
        ("rows", "page", "/socket/bulk"),
        ("rows", "tool", "/socket/bulk"),
        ("send", "send", "/socket"),
        ("config", "get", "/socket"),
    ];
    for (feature, method, _) in calls {
        assert_eq!(call(feature, method, cx).await, Ok(json!({})));
    }
    let went: Vec<(String, String)> = backend
        .on
        .try_iter()
        .filter(|(_, frame)| frame["t"] == "call")
        .map(|(path, frame)| {
            let method = format!(
                "{}/{}",
                frame["feature"].as_str().unwrap(),
                frame["method"].as_str().unwrap()
            );
            (method, path)
        })
        .collect();
    let expected: Vec<(String, String)> = calls
        .iter()
        .map(|(feature, method, path)| (format!("{feature}/{method}"), path.to_string()))
        .collect();
    assert_eq!(went, expected);
}

// With the bulk socket down and the main one up, a large call fails at once and the rest still work.
#[gpui::test]
async fn a_bulk_call_while_the_bulk_socket_is_down_fails_at_once(cx: &mut TestAppContext) {
    let backend = backend();
    // A port nothing listens on: the bulk client's open is refused, so it's disconnected.
    let down = unused_port();
    cx.executor().allow_parking();
    cx.update(|cx| {
        SocketClient::start(endpoint(backend.port), cx);
        SocketClient::start_bulk(endpoint(down), cx);
    });
    assert!(matches!(
        call("rows", "page", cx).await,
        Err(CallError::Failed(_))
    ));
    assert_eq!(call("config", "get", cx).await, Ok(json!({})));
}

fn failure(code: ErrorCode) -> CallFailure {
    CallFailure {
        code,
        message: "the backend's words".into(),
    }
}

#[test]
fn a_failed_call_keeps_its_code_and_the_backends_message() {
    let words = || "the backend's words".to_string();
    assert_eq!(
        call_error(failure(ErrorCode::NotFound)),
        CallError::NotFound(words())
    );
    assert_eq!(
        call_error(failure(ErrorCode::BadArgs)),
        CallError::BadArgs(words())
    );
    assert_eq!(
        call_error(failure(ErrorCode::Failed)),
        CallError::Failed(words())
    );
    assert_eq!(
        call_error(failure(ErrorCode::Timeout)),
        CallError::Timeout(words())
    );
}

#[test]
fn an_unreachable_machine_says_which_machine() {
    let code = ErrorCode::Unreachable {
        machine: "studio".into(),
    };
    assert_eq!(
        call_error(failure(code)),
        CallError::Unreachable {
            machine: "studio".into()
        }
    );
}

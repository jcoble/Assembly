//! `SocketSessionClient` is the window's `SessionClient`: the socket client's rows and calls, with its outcomes read
//! as `CallError`s.

use gpui_kit::*;
use serde_json::Value;

use super::session_client::{CallError, RowsSink, SessionClient};
use crate::core::socket::call_failure::CallFailure;
use crate::core::socket::client::SocketClient;
use crate::core::socket::error_code::ErrorCode;
use crate::core::socket::outcome::Outcome;

pub struct SocketSessionClient;

impl SessionClient for SocketSessionClient {
    fn subscribe_rows(
        &self,
        session: &str,
        after: Option<i64>,
        mut sink: RowsSink,
        cx: &App,
    ) -> Subscription {
        let mut rows = SocketClient::global(cx)
            .read(cx)
            .subscribe_rows(session, after);
        // The task owns the topic subscription, so dropping it unsubscribes.
        let task = cx.spawn(async move |cx| {
            while let Ok(update) = rows.updates.recv().await {
                cx.update(|cx| sink(update, cx));
            }
        });
        Subscription::new(move || drop(task))
    }

    fn call(
        &self,
        feature: &str,
        method: &str,
        args: Value,
        key: Option<&str>,
        cx: &App,
    ) -> Task<Result<Value, CallError>> {
        let call = SocketClient::global(cx)
            .read(cx)
            .call::<Value>(feature, method, args, key);
        cx.foreground_executor().spawn(async move {
            match call.await {
                Outcome::Ok(result) => Ok(result),
                Outcome::Aborted => Err(CallError::Aborted),
                Outcome::Failed(failure) => Err(call_error(failure)),
            }
        })
    }
}

/// A failed call's code, with the backend's message; an unreachable machine says only which.
fn call_error(failure: CallFailure) -> CallError {
    match failure.code {
        ErrorCode::NotFound => CallError::NotFound(failure.message),
        ErrorCode::BadArgs => CallError::BadArgs(failure.message),
        ErrorCode::Failed => CallError::Failed(failure.message),
        ErrorCode::Timeout => CallError::Timeout(failure.message),
        ErrorCode::Unreachable { machine } => CallError::Unreachable { machine },
    }
}

#[cfg(test)]
#[path = "socket_session_client_tests.rs"]
mod tests;

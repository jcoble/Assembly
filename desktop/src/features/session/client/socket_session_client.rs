//! `SocketSessionClient` is the window's `SessionClient`: the socket client's rows and calls, with its outcomes read
//! as `CallError`s.

use gpui_kit::*;
use serde_json::Value;

use super::session_client::{CallError, ClockSink, RowsSink, SessionClient};
use crate::core::clock::backend_clock::BackendClock;
use crate::core::clock::tick::Tick;
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
        // Before the backend's first ready line there's no socket yet, and so no rows.
        let Some(socket) = SocketClient::try_global(cx) else {
            return Subscription::new(|| {});
        };
        let mut rows = socket.read(cx).subscribe_rows(session, after);
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
        let Some(socket) = SocketClient::try_global(cx) else {
            return Task::ready(Err(CallError::Failed("The backend isn't running.".into())));
        };
        let call = socket.read(cx).call::<Value>(feature, method, args, key);
        cx.foreground_executor().spawn(async move {
            match call.await {
                Outcome::Ok(result) => Ok(result),
                Outcome::Aborted => Err(CallError::Aborted),
                Outcome::Failed(failure) => Err(call_error(failure)),
            }
        })
    }

    fn clock(&self, mut sink: ClockSink, cx: &mut App) -> Subscription {
        if SocketClient::try_global(cx).is_none() {
            return Subscription::new(|| {});
        }
        let clock = cx.new(|cx| {
            let mut clock = BackendClock::new();
            clock.hold(cx);
            clock
        });
        let heard = cx.subscribe(&clock, move |_, tick: &Tick, cx| sink(tick.now, cx));
        Subscription::new(move || drop((heard, clock)))
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

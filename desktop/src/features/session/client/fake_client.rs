//! `FakeClient` is a `SessionClient` for tests: it answers calls from what the test queued, records every call and
//! every cancel, and hands rows events to the subscriber when the test pushes them.

#![cfg(test)]

use std::cell::RefCell;
use std::collections::VecDeque;
use std::rc::Rc;

use gpui_kit::*;
use serde_json::Value;

use super::session_client::{CallError, RowsEvent, RowsSink, SessionClient};

#[derive(Clone, Default)]
pub struct FakeClient {
    state: Rc<RefCell<FakeState>>,
}

#[derive(Default)]
struct FakeState {
    answers: VecDeque<Result<Value, CallError>>,
    /// Every call made, as ("feature/method", args).
    calls: Vec<(String, Value)>,
    cancels: usize,
    sink: Option<RowsSink>,
}

impl FakeClient {
    pub fn queue_answer(&self, _answer: Result<Value, CallError>) {
        todo!()
    }

    /// Delivers a rows event to the open subscription, as the socket would.
    pub fn push(&self, _event: RowsEvent, _cx: &mut App) {
        todo!()
    }

    pub fn calls(&self) -> Vec<(String, Value)> {
        todo!()
    }

    pub fn cancels(&self) -> usize {
        todo!()
    }
}

impl SessionClient for FakeClient {
    fn subscribe_rows(&self, _session: &str, _after: Option<i64>, _sink: RowsSink) -> Subscription {
        todo!()
    }

    fn call(
        &self,
        _feature: &str,
        _method: &str,
        _args: Value,
        _key: Option<&str>,
    ) -> Task<Result<Value, CallError>> {
        todo!()
    }
}

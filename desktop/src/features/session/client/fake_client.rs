//! `FakeClient` is a `SessionClient` for tests: it answers pages and calls from what the test queued, records every
//! call, and hands rows to the subscriber when the test pushes them.

#![cfg(test)]

use std::cell::RefCell;
use std::collections::VecDeque;
use std::rc::Rc;

use gpui_kit::*;
use serde_json::Value;

use super::session_client::{CallError, RowPage, SessionClient};
use crate::features::session::rows::row::Row;

#[derive(Clone, Default)]
pub struct FakeClient {
    state: Rc<RefCell<FakeState>>,
}

#[derive(Default)]
struct FakeState {
    pages: VecDeque<Result<RowPage, CallError>>,
    answers: VecDeque<Result<Value, CallError>>,
    /// Every call made, as (feature/method, args).
    calls: Vec<(String, Value)>,
    on_rows: Option<Box<dyn FnMut(Vec<Row>, &mut App)>>,
}

impl FakeClient {
    pub fn queue_page(&self, _page: Result<RowPage, CallError>) {
        todo!()
    }

    pub fn queue_answer(&self, _answer: Result<Value, CallError>) {
        todo!()
    }

    /// Delivers rows to the open subscription, as the socket would.
    pub fn push_rows(&self, _rows: Vec<Row>, _cx: &mut App) {
        todo!()
    }

    pub fn calls(&self) -> Vec<(String, Value)> {
        todo!()
    }
}

impl SessionClient for FakeClient {
    fn rows(&self, _session: &str, _after: Option<i64>, _on_rows: Box<dyn FnMut(Vec<Row>, &mut App)>) -> Subscription {
        todo!()
    }

    fn page(&self, _session: &str, _before: Option<i64>, _limit: u32) -> Task<Result<RowPage, CallError>> {
        todo!()
    }

    fn call(&self, _feature: &str, _method: &str, _args: Value) -> Task<Result<Value, CallError>> {
        todo!()
    }
}

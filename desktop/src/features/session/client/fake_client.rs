//! `FakeClient` is a `SessionClient` for tests: it answers calls from what the test queued, records every call and
//! every cancel, and hands rows events to the subscriber when the test pushes them.

#![cfg(test)]

use std::cell::RefCell;
use std::collections::VecDeque;
use std::rc::Rc;

use gpui_kit::*;
use serde_json::Value;

use super::session_client::{CallError, ClockSink, RowUpdate, RowsSink, SessionClient};

#[derive(Clone, Default)]
pub struct FakeClient {
    state: Rc<RefCell<FakeState>>,
}

#[derive(Default)]
struct FakeState {
    answers: VecDeque<Result<Value, CallError>>,
    /// Every call made, as ("feature/method", args).
    calls: Vec<(String, Value)>,
    /// Every rows subscription, as (session, after).
    subscribed: Vec<(String, Option<i64>)>,
    cancels: usize,
    sink: Option<RowsSink>,
    clock: Option<ClockSink>,
    /// How many clock subscriptions were dropped.
    clock_drops: usize,
}

impl FakeClient {
    /// The next call's answer; a call with none queued fails.
    pub fn queue_answer(&self, answer: Result<Value, CallError>) {
        self.state.borrow_mut().answers.push_back(answer);
    }

    /// Delivers a rows event to the open subscription, as the socket would.
    pub fn push(&self, event: RowUpdate, cx: &mut App) {
        let (sink, cancels) = {
            let mut state = self.state.borrow_mut();
            (state.sink.take(), state.cancels)
        };
        let Some(mut sink) = sink else { return };
        sink(event, cx);
        // Unless the subscription dropped or another took its place while it ran, it's still open.
        let mut state = self.state.borrow_mut();
        if state.cancels == cancels && state.sink.is_none() {
            state.sink = Some(sink);
        }
    }

    /// Delivers a clock tick, as the backend's clock would.
    pub fn tick(&self, now: i64, cx: &mut App) {
        let (sink, drops) = {
            let mut state = self.state.borrow_mut();
            (state.clock.take(), state.clock_drops)
        };
        let Some(mut sink) = sink else { return };
        sink(now, cx);
        let mut state = self.state.borrow_mut();
        if state.clock_drops == drops && state.clock.is_none() {
            state.clock = Some(sink);
        }
    }

    /// The clock is subscribed.
    pub fn ticking(&self) -> bool {
        self.state.borrow().clock.is_some()
    }

    pub fn calls(&self) -> Vec<(String, Value)> {
        self.state.borrow().calls.clone()
    }

    pub fn subscribed(&self) -> Vec<(String, Option<i64>)> {
        self.state.borrow().subscribed.clone()
    }

    /// How many rows subscriptions were dropped.
    pub fn cancels(&self) -> usize {
        self.state.borrow().cancels
    }
}

impl SessionClient for FakeClient {
    fn subscribe_rows(
        &self,
        session: &str,
        after: Option<i64>,
        sink: RowsSink,
        _cx: &App,
    ) -> Subscription {
        let mut state = self.state.borrow_mut();
        state.subscribed.push((session.to_string(), after));
        state.sink = Some(sink);
        let state = self.state.clone();
        Subscription::new(move || {
            let mut state = state.borrow_mut();
            state.cancels += 1;
            state.sink = None;
        })
    }

    fn call(
        &self,
        feature: &str,
        method: &str,
        args: Value,
        _key: Option<&str>,
        _cx: &App,
    ) -> Task<Result<Value, CallError>> {
        let mut state = self.state.borrow_mut();
        state.calls.push((format!("{feature}/{method}"), args));
        let answer = state
            .answers
            .pop_front()
            .unwrap_or_else(|| Err(CallError::Failed("no answer queued".into())));
        Task::ready(answer)
    }

    fn clock(&self, sink: ClockSink, _cx: &mut App) -> Subscription {
        self.state.borrow_mut().clock = Some(sink);
        let state = self.state.clone();
        Subscription::new(move || {
            let mut state = state.borrow_mut();
            state.clock = None;
            state.clock_drops += 1;
        })
    }
}

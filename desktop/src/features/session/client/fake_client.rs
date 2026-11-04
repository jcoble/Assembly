//! `FakeClient` is a `SessionClient` for tests: it answers calls from what the test queued, records every call and
//! every cancel, and hands rows events to the subscriber when the test pushes them. The agent's config is kept apart:
//! the composer reads it whenever it's pointed at a session, so `config/*` calls answer from it and are recorded on
//! their own, and never take an answer queued for another call. So are `sessions/read` calls, which the pane makes as
//! rows land and which always answer.

#![cfg(test)]

use std::cell::RefCell;
use std::collections::{HashMap, VecDeque};
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
    answers: VecDeque<FakeAnswer>,
    next_message: u64,
    message_sinks: HashMap<
        u64,
        (
            String,
            Option<String>,
            Option<super::session_client::MessageSink>,
        ),
    >,
    engine_answers: VecDeque<Result<Value, CallError>>,
    engine_calls: Vec<(String, Value)>,
    /// Every call made, as ("feature/method", args).
    calls: Vec<(String, Value)>,
    /// Every rows subscription, as (session, after).
    subscribed: Vec<(String, Option<i64>)>,
    cancels: usize,
    sink: Option<RowsSink>,
    clock: Option<ClockSink>,
    /// How many clock subscriptions were dropped.
    clock_drops: usize,
    /// What `config/get` and `config/set` answer; `None` fails them.
    config: Option<Value>,
    config_calls: Vec<(String, Value)>,
    /// Every `sessions/read`'s args.
    reads: Vec<Value>,
    read_answers: VecDeque<Result<Value, CallError>>,
}

enum FakeAnswer {
    Value(Result<Value, CallError>),
    OwnerAccepted,
}

impl FakeClient {
    pub fn message_subscriptions(&self) -> usize {
        self.state.borrow().message_sinks.len()
    }

    pub fn push_message(&self, message: &str, machine: Option<&str>, item: Value, cx: &mut App) {
        self.push_message_result(message, machine, Ok(item), cx);
    }
    pub fn push_message_result(
        &self,
        message: &str,
        machine: Option<&str>,
        item: Result<Value, CallError>,
        cx: &mut App,
    ) {
        let ids: Vec<u64> = self
            .state
            .borrow()
            .message_sinks
            .iter()
            .filter(|(_, (m, owner, _))| m == message && owner.as_deref() == machine)
            .map(|(id, _)| *id)
            .collect();
        for id in ids {
            let sink = self
                .state
                .borrow_mut()
                .message_sinks
                .get_mut(&id)
                .and_then(|(_, _, sink)| sink.take());
            if let Some(mut sink) = sink {
                sink(item.clone(), cx);
                if let Some((_, _, target)) = self.state.borrow_mut().message_sinks.get_mut(&id) {
                    *target = Some(sink);
                }
            }
        }
    }

    /// The next call's answer; a call with none queued fails.
    pub fn queue_answer(&self, answer: Result<Value, CallError>) {
        self.state
            .borrow_mut()
            .answers
            .push_back(FakeAnswer::Value(answer));
    }

    pub fn queue_owner_accepted(&self) {
        self.state
            .borrow_mut()
            .answers
            .push_back(FakeAnswer::OwnerAccepted);
    }

    pub fn queue_engine_answer(&self, answer: Result<Value, CallError>) {
        self.state.borrow_mut().engine_answers.push_back(answer);
    }

    pub fn engine_calls(&self) -> Vec<(String, Value)> {
        self.state.borrow().engine_calls.clone()
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

    pub fn set_config(&self, config: Option<Value>) {
        self.state.borrow_mut().config = config;
    }

    /// Every `config/*` call made, as ("config/get", args).
    pub fn config_calls(&self) -> Vec<(String, Value)> {
        self.state.borrow().config_calls.clone()
    }

    pub fn queue_read_answer(&self, answer: Result<Value, CallError>) {
        self.state.borrow_mut().read_answers.push_back(answer);
    }

    pub fn reads(&self) -> Vec<Value> {
        self.state.borrow().reads.clone()
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
        _expected_source: Option<&crate::core::socket::row_source::ExpectedRowSource>,
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

    fn subscribe_message(
        &self,
        message: &str,
        machine: Option<&str>,
        _expected_source: Option<&crate::core::socket::row_source::ExpectedRowSource>,
        sink: super::session_client::MessageSink,
        _cx: &App,
    ) -> Subscription {
        let mut state = self.state.borrow_mut();
        state.next_message += 1;
        let id = state.next_message;
        state.message_sinks.insert(
            id,
            (message.into(), machine.map(str::to_string), Some(sink)),
        );
        let state = self.state.clone();
        Subscription::new(move || {
            state.borrow_mut().message_sinks.remove(&id);
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
        if feature == "config" {
            state
                .config_calls
                .push((format!("{feature}/{method}"), args));
            let config = state.config.clone();
            return Task::ready(config.ok_or_else(|| CallError::Failed("no config".into())));
        }
        if (feature, method) == ("sessions", "read") {
            state.reads.push(args);
            return Task::ready(state.read_answers.pop_front().unwrap_or(Ok(Value::Null)));
        }
        if feature == "engine" {
            state.engine_calls.push((method.to_string(), args.clone()));
            return Task::ready(state.engine_answers.pop_front().unwrap_or_else(|| {
                Ok(serde_json::json!({"engineMachine":args["machine"].as_str().unwrap_or("owner"),"items":[],"nextCreatedMs":null,"nextClientId":null}))
            }));
        }
        state
            .calls
            .push((format!("{feature}/{method}"), args.clone()));
        let answer = state.answers.pop_front().unwrap_or_else(|| {
            FakeAnswer::Value(Err(CallError::Failed("no answer queued".into())))
        });
        let answer = match answer {
            FakeAnswer::Value(answer) => answer,
            FakeAnswer::OwnerAccepted => Ok(serde_json::json!({
                "sessionId":args["session"],"clientId":args["clientId"],
                "messageId":format!("prompt/{}/{}", args["session"].as_str().unwrap(), args["clientId"].as_str().unwrap()),
                "state":"queued","messageVersion":2,"seq":42,"epoch":7,"runId":null
            })),
        };
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

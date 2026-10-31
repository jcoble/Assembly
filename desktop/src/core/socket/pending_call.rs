//! `PendingCall` is a call waiting on its answer, as a future of its `Outcome`. Dropping it before the answer sends
//! `cancel`, so a load nobody wants any more stops; the backend still answers and that answer goes nowhere.

use std::future::Future;
use std::marker::PhantomData;
use std::pin::Pin;
use std::sync::mpsc::Sender;
use std::task::{Context, Poll};

use futures_channel::oneshot::Receiver;
use serde::de::DeserializeOwned;
use serde_json::Value;

use super::call_failure::CallFailure;
use super::error_code::ErrorCode;
use super::event::Event;
use super::outcome::Outcome;

pub struct PendingCall<T> {
    id: u64,
    events: Sender<Event>,
    answer: Receiver<Outcome<Value>>,
    answered: bool,
    result: PhantomData<fn() -> T>,
}

impl<T> PendingCall<T> {
    pub(super) fn new(id: u64, events: Sender<Event>, answer: Receiver<Outcome<Value>>) -> Self {
        Self {
            id,
            events,
            answer,
            answered: false,
            result: PhantomData,
        }
    }
    /// Leaves completion with the socket client, without cancelling the backend when this reply is dropped.
    /// For short operations whose result the window does not display, such as terminal resize and close.
    pub fn ignore_reply(mut self) {
        self.answered = true;
    }
}

impl<T: DeserializeOwned> Future for PendingCall<T> {
    type Output = Outcome<T>;

    /// The answer, with its result read into `T`; a result that doesn't read is `Failed`.
    fn poll(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<Self::Output> {
        let outcome = match Pin::new(&mut self.answer).poll(cx) {
            Poll::Pending => return Poll::Pending,
            // The client thread is gone, which happens only as the app quits.
            Poll::Ready(Err(_)) => Outcome::Aborted,
            Poll::Ready(Ok(Outcome::Ok(result))) => match serde_json::from_value(result) {
                Ok(result) => Outcome::Ok(result),
                Err(error) => {
                    let message = format!("the result didn't read: {error}");
                    Outcome::Failed(CallFailure {
                        code: ErrorCode::Failed,
                        message,
                    })
                }
            },
            Poll::Ready(Ok(Outcome::Failed(failure))) => Outcome::Failed(failure),
            Poll::Ready(Ok(Outcome::Aborted)) => Outcome::Aborted,
        };
        self.answered = true;
        Poll::Ready(outcome)
    }
}

impl<T> Drop for PendingCall<T> {
    fn drop(&mut self) {
        if !self.answered {
            let _ = self.events.send(Event::Cancel { id: self.id });
        }
    }
}

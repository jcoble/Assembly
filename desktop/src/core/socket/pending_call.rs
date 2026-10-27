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

use super::event::Event;
use super::outcome::Outcome;

pub struct PendingCall<T> {
    id: u64,
    events: Sender<Event>,
    answer: Receiver<Outcome<Value>>,
    result: PhantomData<T>,
}

impl<T: DeserializeOwned> Future for PendingCall<T> {
    type Output = Outcome<T>;

    /// The answer, with its result read into `T`; a result that doesn't read is `Failed`.
    fn poll(self: Pin<&mut Self>, _cx: &mut Context<'_>) -> Poll<Self::Output> {
        todo!()
    }
}

impl<T> Drop for PendingCall<T> {
    fn drop(&mut self) {
        todo!()
    }
}

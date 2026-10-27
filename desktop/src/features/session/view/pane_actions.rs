//! The session pane's actions: what a row's button or link asks for, and reading an open tool call's detail.

use std::sync::Arc;

use base64::Engine as _;
use base64::engine::general_purpose::STANDARD;
use gpui_kit::*;
use serde_json::{Value, json};

use super::SessionPane;
use crate::core::intents::open_session::OpenSession;
use crate::features::session::client::session_client::CallError;
use crate::features::session::model::change::Change;
use crate::features::session::model::item::ItemKind;
use crate::features::session::rows::tool::ToolDetail;
use crate::features::session::view::lightbox::open_lightbox;
use crate::features::session::view::session_action::SessionAction;

impl SessionPane {
    /// A row's button or link: the call it makes, or the intent it opens (lane g0-host).
    pub(super) fn on_action(
        &mut self,
        action: SessionAction,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let Some(session) = self.session.clone() else {
            return;
        };
        match action {
            SessionAction::ToggleFold { run_id } => {
                if !self.open_folds.remove(&run_id) {
                    self.open_folds.insert(run_id);
                }
                self.refresh(Change::Nothing, cx);
            }
            SessionAction::Toggle { key } => {
                let kept = self.kept.entry(key.clone()).or_default();
                kept.open = !kept.open;
                if let Some(at) = self.view.items.iter().position(|item| item.key == key) {
                    self.refresh(Change::Updated(at), cx);
                    self.read_detail_if_stale(at, cx);
                }
            }
            SessionAction::ShowAllDiff { key } => {
                self.kept.entry(key.clone()).or_default().all_diff = true;
                if let Some(at) = self.view.items.iter().position(|item| item.key == key) {
                    self.refresh(Change::Updated(at), cx);
                }
            }
            SessionAction::Answer { ask_id, answer } => {
                let args = json!({ "session": session, "ask": ask_id, "answer": answer });
                self.send("answer", args, cx);
            }
            SessionAction::StopTask { task } => {
                self.send("stopTask", json!({ "session": session, "task": task }), cx);
            }
            SessionAction::OpenSession { session_id } => {
                window.dispatch_action(Box::new(OpenSession { id: session_id }), cx);
            }
            SessionAction::ShowImage { name } => self.show_image(session, name, window, cx),
        }
    }

    /// Reads the detail of the tool call at `at` when it's open and its detail was read for another state, or not
    /// read yet: on opening, and when an open call's state changes.
    pub(super) fn read_detail_if_stale(&mut self, at: usize, cx: &mut Context<Self>) {
        let Some(session) = self.session.clone() else {
            return;
        };
        let item = &self.view.items[at];
        let ItemKind::Tool { item_id, tool } = &item.kind else {
            return;
        };
        let kept = self.kept.get(&item.key);
        let fresh = kept
            .and_then(|kept| kept.detail.as_ref())
            .is_some_and(|(state, _)| *state == tool.state);
        if !tool.has_detail || !kept.is_some_and(|kept| kept.open) || fresh {
            return;
        }
        let (key, state) = (item.key.clone(), tool.state);
        let args = json!({ "session": session, "item": item_id });
        let call = self.client.call(
            "rows",
            "tool",
            args,
            Some(&format!("rows/tool:{session}:{item_id}")),
            cx,
        );
        self._tasks.push(cx.spawn(async move |pane, cx| {
            let answer = call.await;
            pane.update(cx, |pane, cx| {
                let detail = answer.and_then(|detail| {
                    serde_json::from_value::<ToolDetail>(detail)
                        .map_err(|error| CallError::Failed(error.to_string()))
                });
                match detail {
                    Ok(detail) => {
                        pane.kept.entry(key.clone()).or_default().detail = Some((state, detail));
                        if let Some(at) = pane.view.items.iter().position(|item| item.key == key) {
                            pane.refresh(Change::Updated(at), cx);
                        }
                    }
                    Err(CallError::Aborted) => {}
                    Err(error) => pane.say_failed("Couldn't read the tool call", error, cx),
                }
            })
            .ok();
        }));
    }

    /// One of the `send` feature's calls; a failure says why under the timeline.
    fn send(&mut self, method: &'static str, args: Value, cx: &mut Context<Self>) {
        let call = self.client.call("send", method, args, None, cx);
        self._tasks.push(cx.spawn(async move |pane, cx| {
            let answer = call.await;
            pane.update(cx, |pane, cx| match answer {
                Ok(_) | Err(CallError::Aborted) => {}
                Err(error) => pane.say_failed("That didn't go through", error, cx),
            })
            .ok();
        }));
    }

    fn say_failed(&mut self, what: &str, error: CallError, cx: &mut Context<Self>) {
        self.status = match error {
            CallError::Aborted => return,
            CallError::Unreachable { machine } => format!("{machine} is offline").into(),
            CallError::NotFound(message)
            | CallError::BadArgs(message)
            | CallError::Failed(message)
            | CallError::Timeout(message) => format!("{what}: {message}").into(),
        };
        cx.notify();
    }
}

impl SessionPane {
    /// Reads an attached image piece by piece (`attachments/read`) and opens it full size.
    fn show_image(
        &mut self,
        session: String,
        name: String,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let client = self.client.clone();
        self._tasks.push(cx.spawn_in(window, async move |pane, cx| {
            let mut bytes: Vec<u8> = Vec::new();
            let (mime, failed) = loop {
                let args = json!({ "session": session, "name": name, "offset": bytes.len() });
                let Ok(call) =
                    cx.update(|_, cx| client.call("attachments", "read", args, None, cx))
                else {
                    return;
                };
                let piece = match call.await {
                    Ok(piece) => piece,
                    Err(error) => break (String::new(), Some(error)),
                };
                let data = piece["data"].as_str().unwrap_or_default().to_string();
                let decoded = cx
                    .background_spawn(async move { STANDARD.decode(data) })
                    .await
                    .unwrap_or_default();
                bytes.extend(decoded.iter());
                let size = piece["size"].as_u64().unwrap_or_default() as usize;
                if decoded.is_empty() || bytes.len() >= size {
                    break (piece["mime"].as_str().unwrap_or_default().to_string(), None);
                }
            };
            let format = ImageFormat::from_mime_type(&mime);
            pane.update_in(cx, |pane, window, cx| match (failed, format) {
                (Some(error), _) => pane.say_failed("Couldn't open the image", error, cx),
                (None, Some(format)) => {
                    let image = Arc::new(Image::from_bytes(format, bytes));
                    open_lightbox(image, name.into(), window, cx);
                }
                (None, None) => pane.say_failed(
                    "Couldn't open the image",
                    CallError::Failed(format!("it isn't an image ({mime})")),
                    cx,
                ),
            })
            .ok();
        }));
    }
}

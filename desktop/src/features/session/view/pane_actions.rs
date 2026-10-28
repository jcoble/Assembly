//! The session pane's actions: what a row's button or link asks for, and reading an open tool call's detail.

use std::rc::Rc;
use std::time::Instant;

use gpui_kit::*;
use serde_json::{Value, json};

use super::SessionPane;
use crate::core::intents::open_session::OpenSession;
use crate::core::theme::grow::GROW;
use crate::features::session::client::session_client::CallError;
use crate::features::session::model::change::Change;
use crate::features::session::model::item::ItemKind;
use crate::features::session::rows::tool::ToolDetail;
use crate::features::session::view::ask_row::AskForm;
use crate::features::session::view::grow::toggled;
use crate::features::session::view::lightbox::{open_lightbox, read_image};
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
                // A fold's work stays on the list while it shuts, and goes when it's done (ease_frame).
                let key = format!("fold:{run_id}");
                let kept = self.kept.entry(key.clone()).or_default();
                kept.open = !kept.open;
                kept.toggled_at = Some(toggled(kept.open, kept.toggled_at, Instant::now(), GROW));
                if kept.open {
                    self.open_folds.insert(run_id);
                    self.refresh(Change::Nothing, cx);
                }
                self.start_growing(key, cx);
            }
            SessionAction::Toggle { key } => {
                let kept = self.kept.entry(key.clone()).or_default();
                kept.open = !kept.open;
                if let Some(at) = self.view.items.iter().position(|item| item.key == key) {
                    self.refresh(Change::Updated(at), cx);
                }
            }
            SessionAction::RetryPage => {
                if let Some(before) = self.retry_first {
                    self.status = SharedString::default();
                    self.load_page(before, true, cx);
                }
            }
            SessionAction::TogglePlan => {
                self.plan_open = !self.plan_open;
                cx.notify();
            }
            SessionAction::ShowAllDiff { key } => {
                self.kept.entry(key.clone()).or_default().all_diff = true;
                if let Some(at) = self.view.items.iter().position(|item| item.key == key) {
                    self.refresh(Change::Updated(at), cx);
                }
            }
            SessionAction::Pick {
                ask_id,
                field,
                value,
                many,
            } => {
                let Some(form) = self.forms.get_mut(&ask_id) else {
                    return;
                };
                if many {
                    let list = form
                        .picked
                        .entry(field)
                        .or_insert_with(|| Value::Array(Vec::new()));
                    if let Value::Array(list) = list {
                        match list.iter().position(|it| it.as_str() == Some(&value)) {
                            Some(at) => drop(list.remove(at)),
                            None => list.push(Value::String(value)),
                        }
                    }
                } else {
                    form.picked.insert(field, Value::String(value));
                }
                self.refresh_ask(&ask_id, cx);
            }
            SessionAction::SubmitAsk { ask_id } => {
                let Some(form) = self.forms.get(&ask_id) else {
                    return;
                };
                let texts = form
                    .texts
                    .iter()
                    .map(|(name, input)| (name.clone(), input.read(cx).value().to_string()))
                    .collect();
                let answer = AskForm::content(&form.picked, texts);
                self.on_action(SessionAction::Answer { ask_id, answer }, window, cx);
            }
            SessionAction::Answer { ask_id, answer } => {
                // A settled ask still shows its controls while it shrinks; they answer nothing more.
                let open = self.view.items.iter().any(|item| {
                    matches!(&item.kind, ItemKind::Ask { ask_id: id, ask } if *id == ask_id && ask.state == "open")
                });
                if !open {
                    return;
                }
                self.status = SharedString::default();
                let task = format!("answer:{ask_id}");
                let args = json!({ "session": session, "ask": ask_id, "answer": answer });
                self.send(task, "answer", args, cx);
            }
            SessionAction::StopTask { task } => {
                self.status = SharedString::default();
                let args = json!({ "session": session, "task": task });
                self.send(format!("stop:{task}"), "stopTask", args, cx);
            }
            SessionAction::OpenSession { session_id } => {
                window.dispatch_action(Box::new(OpenSession { id: session_id }), cx);
            }
            SessionAction::ShowImage { name } => {
                self.status = SharedString::default();
                self.show_image(session, name, window, cx)
            }
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
        let asked = self.reading.get(&item.key) == Some(&tool.state);
        if !tool.has_detail || !kept.is_some_and(|kept| kept.open) || fresh || asked {
            return;
        }
        let (key, state) = (item.key.clone(), tool.state);
        self.reading.insert(key.clone(), state);
        let args = json!({ "session": session, "item": item_id });
        let call = self.client.call(
            "rows",
            "tool",
            args,
            Some(&format!("rows/tool:{session}:{item_id}")),
            cx,
        );
        let task = format!("tool:{key}");
        self._loads.insert(
            task,
            cx.spawn(async move |pane, cx| {
                let answer = call.await;
                pane.update(cx, |pane, cx| {
                    if pane.reading.get(&key) == Some(&state) {
                        pane.reading.remove(&key);
                    }
                    let detail = answer.and_then(|detail| {
                        serde_json::from_value::<ToolDetail>(detail)
                            .map_err(|error| CallError::Failed(error.to_string()))
                    });
                    match detail {
                        Ok(detail) => {
                            // A call opened before its first detail came grows into it now (its ExpandRow).
                            pane.kept.entry(key.clone()).or_default().detail =
                                Some((state, Rc::new(detail)));
                            if let Some(at) =
                                pane.view.items.iter().position(|item| item.key == key)
                            {
                                pane.refresh(Change::Updated(at), cx);
                            }
                        }
                        Err(CallError::Aborted) => {}
                        Err(error) => pane.say_failed("Couldn't read the tool call", error, cx),
                    }
                })
                .ok();
            }),
        );
    }

    /// Starts a fold's ease (`fold:<run>`): the next draw runs its frames, unless they're running.
    pub(super) fn start_growing(&mut self, key: String, cx: &mut Context<Self>) {
        self.growing.insert(key);
        cx.notify();
    }

    /// One frame of the folds growing: the list measures each one's work again, so the rows below slide with it. An
    /// ease that's done is dropped, and a fold that has shut takes its work off the list.
    pub(super) fn grow_frame(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.grow_asked = false;
        let (now, still) = (Instant::now(), cx.reduce_motion());
        let growing: Vec<String> = self.growing.iter().cloned().collect();
        let mut shut = false;
        for key in growing {
            let kept = self.kept.get(&key);
            let moving = !still
                && kept
                    .and_then(|kept| kept.toggled_at)
                    .is_some_and(|at| now.duration_since(at) < GROW);
            let rows: Vec<String> = key
                .strip_prefix("fold:")
                .and_then(|run| self.folds.get(run))
                .map(|fold| fold.work.iter().cloned().collect())
                .unwrap_or_default();
            self.timeline.update(cx, |timeline, cx| {
                rows.iter().for_each(|row| timeline.redraw_row(row, cx))
            });
            if !moving {
                let open = kept.is_some_and(|kept| kept.open);
                if let (Some(run), false) = (key.strip_prefix("fold:"), open) {
                    shut |= self.open_folds.remove(run);
                }
                self.growing.remove(&key);
            }
        }
        if shut {
            self.refresh(Change::Nothing, cx);
        }
        if !self.growing.is_empty() {
            self.grow_asked = true;
            cx.on_next_frame(window, |pane, window, cx| pane.grow_frame(window, cx));
        }
    }

    /// Draws an ask's card again.
    fn refresh_ask(&mut self, ask_id: &str, cx: &mut Context<Self>) {
        let at = self.view.items.iter().position(
            |item| matches!(&item.kind, ItemKind::Ask { ask_id: id, .. } if id == ask_id),
        );
        if let Some(at) = at {
            self.refresh(Change::Updated(at), cx);
        }
    }

    /// One of the `send` feature's calls, `task` naming what it's for; a failure says why under the timeline.
    fn send(&mut self, task: String, method: &'static str, args: Value, cx: &mut Context<Self>) {
        let call = self.client.call("send", method, args, None, cx);
        let session = self.session.clone();
        self._writes.insert(
            task,
            cx.spawn(async move |pane, cx| {
                let answer = call.await;
                pane.update(cx, |pane, cx| match answer {
                    Ok(_) | Err(CallError::Aborted) => {}
                    // Said only where it was sent from: the reader may have switched away since.
                    Err(_) if pane.session != session => {}
                    Err(error) => pane.say_failed("That didn't go through", error, cx),
                })
                .ok();
            }),
        );
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
    /// Reads an attached image and opens it full size.
    fn show_image(
        &mut self,
        session: String,
        name: String,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let client = self.client.clone();
        let task = format!("image:{name}");
        self._loads.insert(
            task,
            cx.spawn_in(window, async move |pane, cx| {
                let image = read_image(client, session, name.clone(), cx).await;
                pane.update_in(cx, |pane, window, cx| match image {
                    Ok(image) => open_lightbox(image, name.into(), window, cx),
                    Err(error) => pane.say_failed("Couldn't open the image", error, cx),
                })
                .ok();
            }),
        );
    }
}

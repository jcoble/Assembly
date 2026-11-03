//! The session pane's actions: what a row's button or link asks for, and reading an open tool call's detail.

use std::rc::Rc;
use std::time::Instant;

use gpui_kit::*;
use serde_json::{Value, json};

use super::SessionPane;
use crate::core::theme::grow::GROW;
use crate::features::session::client::session_client::CallError;
use crate::features::session::model::change::Change;
use crate::features::session::model::item::ItemKind;
use crate::features::session::rows::tool::ToolDetail;
use crate::core::ui::ask::form::AskForm;
use crate::features::session::view::grow::toggled;
use crate::features::session::view::lightbox::{open_lightbox, read_image};
use crate::features::session::view::session_action::SessionAction;

impl SessionPane {
    /// A qualified read refusal invalidates only the matching visible source, never another selection.
    pub(crate) fn refuse_image_source(session:&str,source:&crate::core::socket::row_source::ExpectedRowSource,reason:String,cx:&mut App) {
        if let Some(pane)=crate::core::ui::center_panes::CenterPanes::find::<SessionPane>("Chat",cx) {
            pane.update(cx,|pane,cx| {
                if pane.session.as_deref()==Some(session)&&pane.row_source.as_ref()==Some(source) {
                    pane.on_rows(crate::features::session::client::session_client::RowUpdate::Refused{code:"source_changed".into(),message:reason},cx);
                }
            });
        }
    }

    /// A row's button or link: the call it makes, or the intent it opens (lane g0-host).
    pub(super) fn on_action(
        &mut self,
        action: SessionAction,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if self.active_source_cut.as_ref().is_some_and(|cut|!cut.current(cx)) {
            self.composer.update(cx,|composer,cx|composer.invalidate_source(cx));
            self.status="The captured session connection changed. Open the session again.".into();cx.notify();return;
        }
        if self.row_source.is_some() && self._rows.is_none() {
            self.status = "The captured session source is unavailable. Open the session again.".into(); cx.notify(); return;
        }
        if self.read_only && matches!(&action,
            SessionAction::RetryPrompt { .. } | SessionAction::Pick { .. }
            | SessionAction::SubmitAsk { .. } | SessionAction::Answer { .. } | SessionAction::StopTask { .. } | SessionAction::StopNative { .. } | SessionAction::StopEngineChild { .. }) {
            self.status = "This recorded transcript is read-only.".into(); cx.notify(); return;
        }
        let Some(session) = self.session.clone() else {
            return;
        };
        match action {
            SessionAction::RetryPrompt { client_id } => {
                self.composer.update(cx, |composer, cx| {
                    composer.retry_pending(&client_id, window, cx)
                });
            }
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
                form.pick(field,value,many);
                self.refresh_ask(&ask_id, cx);
                cx.notify();
            }
            SessionAction::SubmitAsk { ask_id } => {
                let Some(form) = self.forms.get(&ask_id) else {
                    return;
                };
                let texts = form
                    .texts
                    .iter()
                    .map(|(name, input)| (name.clone(), input.read(cx).value().to_string()))
                    .collect::<Vec<_>>();
                let asked = self.view.items.iter().find_map(|item| match &item.kind {
                    ItemKind::Ask { ask_id: id, ask } if *id == ask_id => Some(ask),
                    _ => None,
                });
                if let Some(label) = asked.and_then(|ask| AskForm::missing(ask, &form.picked, &texts)) {
                    self.status = format!("{label} is required.").into();
                    cx.notify();
                    return;
                }
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
            SessionAction::StopNative { key, payload_session_id } => {
                match self.native_stop_task(&key,&payload_session_id) {
                    Ok(task)=>{
                        self.status=SharedString::default();
                        self.send(format!("stop:{task}"),"stopTask",json!({"session":session,"task":task}),cx);
                    }
                    Err(error)=>{self.status=error.message.into();cx.notify();}
                }
            }
            SessionAction::StopTask { task } => {
                self.status = SharedString::default();
                let args = json!({ "session": session, "task": task });
                self.send(format!("stop:{task}"), "stopTask", args, cx);
            }
            SessionAction::StopEngineChild{key,session_id}=>self.stop_engine_child(&key,&session_id,cx),
            SessionAction::OpenNative { key, payload_session_id } => {
                match self.native_open_request(&key,&payload_session_id) {
                    Ok(request)=>self.request_open(request,window,cx),
                    Err(error)=>{self.status=error.message.into();cx.notify();}
                }
            }
            SessionAction::OpenEngineChild { session_id } => {
                let parent=self.row_source.as_ref().map(|source|crate::core::engine::SessionKey{machine:source.machine.clone(),id:session.clone()});
                let feed=crate::core::engine::EngineFeed::global(cx);
                let children=parent.map(|parent|feed.read(cx).children_of(&parent)).unwrap_or_default();
                let mut matches=children.into_iter().filter(|child|child.key.id==session_id);
                let key=matches.next().map(|child|child.session_key());
                if let Some(key)=key.filter(|_|matches.next().is_none()) {
                    self.request_open(crate::core::intents::session_open::SessionOpenRequest{key,request_id:uuid::Uuid::new_v4().to_string(),target:crate::core::intents::session_open::SessionOpenTarget::Engine},window,cx);
                }else{self.status="The helper's source-qualified session is unavailable.".into();cx.notify();}
            }
            SessionAction::OpenSender {key} => {
                self.request_open(crate::core::intents::session_open::SessionOpenRequest{key,request_id:uuid::Uuid::new_v4().to_string(),target:crate::core::intents::session_open::SessionOpenTarget::Engine},window,cx);
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
    fn send(&mut self, task: String, method: &'static str, mut args: Value, cx: &mut Context<Self>) {
        let source=self.row_source.clone();
        if let Some(source)=&source{args["expectedSource"]=json!(source);}
        let call = self.client.call("send", method, args, None, cx);
        let session = self.session.clone();
        self._writes.insert(
            task,
            cx.spawn(async move |pane, cx| {
                let answer = call.await;
                pane.update(cx, |pane, cx| match answer {
                    Ok(_) | Err(CallError::Aborted) => {}
                    // Said only where it was sent from: the reader may have switched away since.
                    Err(_) if pane.session != session || pane.row_source!=source => {}
                    Err(error) => {
                        if matches!(&error,CallError::Refused{code,..} if code=="source_changed") {
                            pane._rows=None;pane.active_source_cut=None;pane.composer.update(cx,|composer,cx|composer.invalidate_source(cx));
                        }
                        pane.say_failed("That didn't go through", error, cx);
                    },
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
            | CallError::Refused { message, .. }
            | CallError::Uncertain { message, .. }
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
        if self.session.as_ref()!=Some(&session){return;}
        let source=self.row_source.clone();let serial=self.active_open_serial;
        let client = self.client.clone();
        let task = format!("image:{name}");
        self._loads.insert(
            task,
            cx.spawn_in(window, async move |pane, cx| {
                let image = read_image(client, session.clone(), name.clone(), source.clone(), cx).await;
                pane.update_in(cx, |pane, window, cx| {
                    if pane.session.as_ref()!=Some(&session)||pane.row_source!=source||pane.active_open_serial!=serial{return;}
                    match image {
                        Ok(image)=>open_lightbox(image,name.into(),window,cx),
                        Err(error)=>{
                            if matches!(&error,CallError::Refused{code,..} if code=="source_changed") {
                                pane.on_rows(crate::features::session::client::session_client::RowUpdate::Refused{code:"source_changed".into(),message:error.reason()},cx);
                            }
                            pane.say_failed("Couldn't open the image",error,cx);
                        }
                    }
                })
                .ok();
            }),
        );
    }
}

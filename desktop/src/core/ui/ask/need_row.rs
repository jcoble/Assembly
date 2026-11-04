//! Inline need answers use the sole shared Engine feed and its existing checked command path.
use crate::core::{
    engine::{
        EngineChanged, EngineFeed, EngineKey, EngineRefusal, EngineScope, SessionKey,
        commands::NeedAnswer,
        engine_items::{NO_ANSWER_CONTROLS, NeedItem},
    },
    ui::ask::{
        controls::{self, ControlAction},
        form::AskForm,
    },
};
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::input::{Textarea, TextareaState};
use gpui_kit::component::{ActiveTheme as _, h_flex, v_flex};
use gpui_kit::*;
use serde_json::{Value, json};
use std::rc::Rc;
pub(super) fn need_relation(
    need: &NeedItem,
    viewed: &SessionKey,
) -> Option<(&'static str, SessionKey)> {
    let addressee = need.addressee()?;
    if addressee == need.session {
        return None;
    }
    if viewed == &addressee {
        Some(("From", need.session.clone()))
    } else if viewed == &need.session {
        Some(("With", addressee))
    } else {
        None
    }
}
#[derive(Clone, Debug)]
pub enum NeedRowState {
    Idle,
    Open,
    Sending,
    Answered { here: bool, by: SharedString },
    Closed { state: SharedString },
    Refused { reason: String },
    Stale { reason: String },
    Failed { message: String },
}
pub(super) fn settled_state(need: &NeedItem, here: bool) -> NeedRowState {
    if need.state != "answered" {
        return NeedRowState::Closed {
            state: need.state.clone().into(),
        };
    }
    let by = need
        .answered_origin
        .as_ref()
        .map(|origin| match origin.surface.as_deref() {
            Some(surface) => format!("in {surface}"),
            None => format!("on {}", origin.channel),
        })
        .or_else(|| need.answered_by.clone())
        .unwrap_or_else(|| "elsewhere".into());
    NeedRowState::Answered {
        here,
        by: by.into(),
    }
}
pub(super) fn failure_state(error: &EngineRefusal) -> NeedRowState {
    match error.code.as_str() {
        "version_conflict" => NeedRowState::Open,
        "stale_epoch" => NeedRowState::Stale {
            reason: "The session changed hands. Check and send again.".into(),
        },
        "already_resolved" => NeedRowState::Refused {
            reason: error.message.clone(),
        },
        _ => NeedRowState::Failed {
            message: error.message.clone(),
        },
    }
}
pub(super) fn typed_answer(need: &NeedItem, answer: Value) -> Result<Value, String> {
    let presentation = need.presentation.as_ref().ok_or(NO_ANSWER_CONTROLS)?;
    match presentation.input_mode.as_str() {
        "permission"
            if answer.as_object().is_some_and(|object| object.len() == 1)
                && answer["optionId"].as_str().is_some_and(|id| {
                    presentation.options.iter().any(|option| option.id == id)
                }) =>
        {
            Ok(answer)
        }
        "choice"
            if answer
                .as_str()
                .is_some_and(|id| presentation.options.iter().any(|option| option.id == id)) =>
        {
            Ok(answer)
        }
        "text"
            if answer
                .as_str()
                .is_some_and(|text| text.chars().count() <= 600) =>
        {
            Ok(answer)
        }
        "form"
            if presentation.schema.is_some()
                && matches!(
                    answer["action"].as_str(),
                    Some("accept" | "decline" | "cancel")
                ) =>
        {
            Ok(answer)
        }
        _ => Err("Review the supplied question and its current answer controls.".into()),
    }
}
pub(super) fn validate_scope(key: &EngineKey, scope: &EngineScope) -> Result<(), EngineRefusal> {
    if scope.machine == key.machine
        && key.kind == "needs_me"
        && scope.topic == "needs_me"
        && scope.key.is_none()
    {
        Ok(())
    } else {
        Err(EngineRefusal {
            code: "need_scope_mismatch".into(),
            message: "The question does not belong to this machine-wide needs view.".into(),
        })
    }
}
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum NeedRowEvent {
    Settled,
}
pub struct NeedRow {
    key: EngineKey,
    scope: EngineScope,
    feed: Entity<EngineFeed>,
    surface: &'static str,
    viewed: Option<SessionKey>,
    need: NeedItem,
    state: NeedRowState,
    available: bool,
    text: Entity<TextareaState>,
    form: Option<AskForm>,
    serial: u64,
    accepted_here: bool,
    reconciled: bool,
    settled: bool,
    unsent: Option<String>,
    offline: Option<SharedString>,
    _changed: Subscription,
    _send: Option<Task<()>>,
    _read: Option<Task<()>>,
}
impl EventEmitter<NeedRowEvent> for NeedRow {}
impl NeedRow {
    pub fn need(&self) -> &NeedItem {
        &self.need
    }
    /// What was being typed when someone else answered. It's shown to copy, and never sent.
    pub fn unsent_draft(&self) -> Option<&str> {
        self.unsent.as_deref()
    }
    /// Why this need's machine can't be answered on right now. Its draft stays, and nothing is sent until it's back.
    pub fn set_offline(&mut self, reason: Option<SharedString>, cx: &mut Context<Self>) {
        if self.offline != reason {
            self.offline = reason;
            cx.notify();
        }
    }
    pub fn create(
        key: EngineKey,
        scope: EngineScope,
        feed: Entity<EngineFeed>,
        surface: &'static str,
        window: &mut Window,
        cx: &mut App,
    ) -> Result<Entity<Self>, EngineRefusal> {
        validate_scope(&key, &scope)?;
        if !feed.read(cx).loaded(&scope) {
            return Err(EngineRefusal {
                code: "need_source_unavailable".into(),
                message: "The owning needs view is not coherent yet.".into(),
            });
        }
        let need = feed.read(cx).need(&key).ok_or_else(|| EngineRefusal {
            code: "need_unavailable".into(),
            message: "The current question is unavailable.".into(),
        })?;
        Ok(cx.new(|cx| Self::build(key, scope, feed, surface, need, window, cx)))
    }
    pub fn create_for(
        viewed: SessionKey,
        key: EngineKey,
        scope: EngineScope,
        feed: Entity<EngineFeed>,
        surface: &'static str,
        window: &mut Window,
        cx: &mut App,
    ) -> Result<Entity<Self>, EngineRefusal> {
        let row = Self::create(key, scope, feed, surface, window, cx)?;
        row.update(cx, |row, _| row.viewed = Some(viewed));
        Ok(row)
    }
    fn build(
        key: EngineKey,
        scope: EngineScope,
        feed: Entity<EngineFeed>,
        surface: &'static str,
        need: NeedItem,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Self {
        let changed = cx.subscribe(&feed, |row, _, event: &EngineChanged, cx| {
            if event.keys.contains(&row.key)
                || event.sessions.contains(&row.need.session)
                || event.scopes.contains(&row.scope)
            {
                row.refresh(cx);
            }
        });
        let text = cx.new(|cx| {
            TextareaState::new(window, cx)
                .auto_grow(1, 6)
                .submit_on_enter(true)
                .placeholder("Type your answer")
        });
        let settled = need.state != "open";
        let state = if settled {
            settled_state(&need, false)
        } else {
            NeedRowState::Idle
        };
        Self {
            key,
            scope,
            feed,
            surface,
            viewed: None,
            need,
            state,
            available: true,
            text,
            form: None,
            serial: 0,
            accepted_here: false,
            reconciled: true,
            settled,
            unsent: None,
            offline: None,
            _changed: changed,
            _send: None,
            _read: None,
        }
    }
    fn refresh(&mut self, cx: &mut Context<Self>) {
        let next = self.feed.read(cx).need(&self.key);
        self.available = self.feed.read(cx).loaded(&self.scope) && next.is_some();
        if let Some(need) = next.filter(|need| need.version >= self.need.version) {
            self.need = need;
            if self.need.state != "open" {
                self.state = settled_state(&self.need, self.accepted_here);
                self.note_settled(cx);
            }
        }
        cx.notify();
    }
    /// Once, when the need stops being open. An answer of ours still on its way isn't known yet, so it waits for the reread.
    fn note_settled(&mut self, cx: &mut Context<Self>) {
        if self.settled || !self.reconciled || self.need.state == "open" {
            return;
        }
        self.settled = true;
        if !self.accepted_here {
            let draft = self.text.read(cx).value().to_string();
            if !draft.trim().is_empty() {
                self.unsent = Some(draft);
            }
        }
        cx.emit(NeedRowEvent::Settled);
    }
    pub fn open(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.refresh(cx);
        if !self.available || self.need.state != "open" {
            return;
        }
        if self.form.is_none() {
            self.form = self
                .need
                .presentation
                .as_ref()
                .and_then(|presentation| {
                    presentation.provider_payload("open", self.need.text.clone(), None)
                })
                .and_then(|payload| AskForm::of(&payload, window, cx));
        }
        self.state = NeedRowState::Open;
        cx.notify();
    }
    fn control(&mut self, action: ControlAction, cx: &mut Context<Self>) {
        self.refresh(cx);
        if matches!(self.state, NeedRowState::Sending) || !self.available || !self.reconciled {
            return;
        }
        match action {
            ControlAction::Pick { field, value, many } => {
                if let Some(form) = self.form.as_mut() {
                    form.pick(field, value, many);
                    cx.notify();
                }
            }
            ControlAction::Flag { field, on } => {
                if let Some(form) = self.form.as_mut() {
                    form.set_flag(field, on);
                    cx.notify();
                }
            }
            ControlAction::Submit => {
                let Some(form) = &self.form else {
                    return;
                };
                let Some(asked) = self.need.presentation.as_ref().and_then(|presentation| {
                    presentation.provider_payload("open", self.need.text.clone(), None)
                }) else {
                    return;
                };
                let texts = form
                    .texts
                    .iter()
                    .map(|(name, input)| (name.clone(), input.read(cx).value().to_string()))
                    .collect();
                match AskForm::answer(&asked, &form.picked, texts) {
                    Ok(answer) => self.answer(NeedAnswer::Answer(answer), cx),
                    Err(message) => {
                        self.state = NeedRowState::Failed { message };
                        cx.notify();
                    }
                }
            }
            ControlAction::Answer(answer) => self.answer(NeedAnswer::Answer(answer), cx),
        }
    }
    fn answer(&mut self, answer: NeedAnswer, cx: &mut Context<Self>) {
        self.refresh(cx);
        if self.offline.is_some()
            || !self.available
            || !self.reconciled
            || self.need.state != "open"
            || matches!(self.state, NeedRowState::Sending)
        {
            return;
        }
        let answer = match answer {
            NeedAnswer::Answer(answer) => match typed_answer(&self.need, answer) {
                Ok(answer) => NeedAnswer::Answer(answer),
                Err(message) => {
                    self.state = NeedRowState::Failed { message };
                    cx.notify();
                    return;
                }
            },
            NeedAnswer::Dismiss => NeedAnswer::Dismiss,
        };
        self.serial += 1;
        let serial = self.serial;
        self.accepted_here = false;
        self.reconciled = false;
        self.state = NeedRowState::Sending;
        let call = self.feed.update(cx, |feed, cx| {
            feed.resolve_need(self.key.clone(), answer, Some(self.surface), cx)
        });
        self._send = Some(cx.spawn(async move |row, cx| {
            let result = call.await;
            let _ = row.update(cx, |row, cx| {
                if row.serial != serial {
                    return;
                }
                match result {
                    Ok(receipt) if receipt.applied => row.accepted_here = true,
                    Ok(receipt) => {
                        row.state = failure_state(&EngineRefusal {
                            code: receipt.error.clone().unwrap_or_else(|| "refused".into()),
                            message: receipt
                                .error
                                .unwrap_or_else(|| "The answer was refused.".into()),
                        })
                    }
                    Err(error) => row.state = failure_state(&error),
                }
                row.reconcile(serial, cx);
                cx.notify();
            });
        }));
        cx.notify();
    }
    fn reconcile(&mut self, serial: u64, cx: &mut Context<Self>) {
        let refresh_session = matches!(self.state, NeedRowState::Stale { .. }).then(|| {
            self.feed.update(cx, |feed, cx| {
                feed.ensure_item(
                    EngineKey {
                        machine: self.need.session.machine.clone(),
                        kind: "session".into(),
                        id: self.need.session.id.clone(),
                    },
                    cx,
                )
            })
        });
        self._read=Some(cx.spawn(async move|row,cx| {
            if let Some(refresh)=refresh_session {
                let answer=refresh.await;
                if !matches!(answer,Ok(Some(_))) {
                    let _=row.update(cx,|row,cx|{if row.serial==serial{row.reconciled=false;row.state=NeedRowState::Stale{reason:"The owning session could not be revalidated. Your answer was kept.".into()};cx.notify();}});return;
                }
            }
            let call=row.update(cx,|row,cx| {
                (row.serial==serial).then(||row.feed.update(cx,|feed,cx|feed.ensure_item(row.key.clone(),cx)))
            }).ok().flatten();
            let Some(call)=call else{return;};
            let result=call.await;
            let _=row.update(cx,|row,cx| {
                if row.serial!=serial{return;}
                match result {
                    Ok(Some(item))=>if let Some(need)=NeedItem::of(&item) {
                        row.need=need;row.available=row.feed.read(cx).loaded(&row.scope);row.reconciled=true;
                        if row.need.state!="open"{row.state=settled_state(&row.need,row.accepted_here);row.note_settled(cx);}
                        else if matches!(row.state,NeedRowState::Sending){row.state=NeedRowState::Open;}
                    }else{row.available=false;},
                    Ok(None)=>row.available=false,
                    Err(error)=>{row.state=failure_state(&error);row.reconciled=false;},
                }
                cx.notify();
            });
        }));
    }
}
#[path = "need_row_render.rs"]
mod render;

#[cfg(test)]
#[path = "need_interaction_tests.rs"]
mod interaction_tests;

#[cfg(test)]
#[path = "need_notice_tests.rs"]
mod notice_tests;

use super::{Created, DraftEvent, NewSessionForm};
use super::start_request::{CheckoutIntent, StartRequest};
use super::start_source::StartSource;
use super::create_reply::{AcceptedCreate, CreateReply};
use super::checkout_wait::{self, CheckoutObservation};
use crate::core::{engine::{EngineChanged,EngineFeed,EngineKey,EngineScope,EngineWatch,SessionKey,commands::WindowCommand},
    intents::session_open::{SessionOpenRequest,SessionOpenResult,SessionOpenTarget},state::selected_session::SelectedSession};
use crate::features::session::{client::session_client::CallError,composer::composer_view::{ComposerEvent,StagedAttachment},view::session_pane::SessionPane};
use gpui_kit::*;
use serde_json::json;
use std::rc::Rc;

pub(super) struct StartAttempt {
    request:StartRequest,text:String,images:Vec<StagedAttachment>,source:Option<Rc<StartSource>>,
    accepted:Option<AcceptedCreate>,current_operation:Option<String>,retry_operation:Option<String>,retry_version:i64,
    lookup_absent:bool,checkout_refused:bool,opening:Option<String>,prompt_dispatched:bool,
    window:AnyWindowHandle,watch:Option<EngineWatch>,changed:Option<Subscription>,
    connections:Vec<Subscription>,navigation:Option<Subscription>,prompt:Option<Subscription>,
}
impl NewSessionForm {
    pub(super) fn first_send(&mut self,text:String,images:Vec<StagedAttachment>,window:&mut Window,cx:&mut Context<Self>) {
        if self.starting {return;}
        if let Some(attempt)=self.attempt.as_mut() {
            if attempt.prompt_dispatched {self.start_error("The original prompt is being reconciled in Chat. It will not be sent again.",cx);return;}
            attempt.text=text;attempt.images=images;
            if attempt.accepted.is_some() {
                if attempt.checkout_refused {self.retry_initial_checkout(cx);}else{self.observe_start(cx);}
                return;
            }
            let lookup=!attempt.lookup_absent;
            self.resolve_start(lookup,cx);
            return;
        }
        let (Some(config),Some(target),Some(agent))=(&self.config,self.selected_target(),self.agent.as_ref())else{
            self.start_error("Choose ready settings advertised by this machine.",cx);return;
        };
        if !config.ready()||self.location_source.as_ref().is_none_or(|source|!source.current(cx)) {
            self.start_error("Revalidate this project's current machine and agent settings before sending.",cx);return;
        }
        if self.selected_project.is_some()&&self.inspection.is_none(){self.start_error("The selected project inspection is unavailable.",cx);return;}
        let checkout=self.inspection.as_ref().filter(|inspection|inspection.is_git).map(|_|CheckoutIntent::Current);
        let request=match StartRequest::capture(agent.clone(),self.selected_project.clone(),self.machine.clone(),checkout,self.location_source.as_ref().expect("checked location source").source.clone(),
            json!({"targetId":target,"choices":config.config.choices})) {
            Ok(request)=>request,Err(error)=>{self.start_error(&error,cx);return;}
        };
        self.attempt=Some(StartAttempt{request,text,images,source:None,accepted:None,current_operation:None,retry_operation:None,retry_version:0,
            lookup_absent:false,checkout_refused:false,opening:None,prompt_dispatched:false,window:window.window_handle(),
            watch:None,changed:None,connections:Vec::new(),navigation:None,prompt:None});
        self.project_chooser.update(cx,|chooser,cx|chooser.set_locked(true,cx));
        self.resolve_start(false,cx);
    }
    pub(super) fn reconcile_start(&mut self,cx:&mut Context<Self>) {
        if self.starting||self.attempt.is_none(){return;}
        if let Some(attempt)=self.attempt.as_ref()
            && let Some(accepted)=attempt.accepted.as_ref()
            && (!attempt.request.requires_checkout()||accepted.initial_operation_id.is_some()) {
            self.read_start_entity(cx);return;
        }
        self.resolve_start(true,cx);
    }
    fn resolve_start(&mut self,lookup:bool,cx:&mut Context<Self>) {
        let Some(attempt)=self.attempt.as_ref()else{return;};
        let route=attempt.request.machine().map(String::from);
        let resolved=StartSource::resolve(route,cx);
        self.starting=true;self.start_composer.update(cx,|composer,cx|composer.set_start_state(false,true,None,cx));
        self._create=Some(cx.spawn(async move|form,cx| {
            let resolved=resolved.await;
            let call=form.update(cx,|form,cx| {
                let source=match resolved {Ok(source)=>Rc::new(source),Err(error)=>{form.start_error(&error,cx);return None;}};
                let original=form.attempt.as_ref().map(|attempt|attempt.request.source());
                if original.is_none_or(|original|original!=&source.source) {
                    form.start_error("The original start source changed. The request and draft were retained.",cx);return None;
                }
                let Some(attempt)=form.attempt.as_mut()else{return None;};
                attempt.source=Some(source.clone());
                attempt.connections=source.sockets().iter().map(|socket|cx.observe(socket,|form:&mut Self,_,cx|{
                    if form.attempt.as_ref().and_then(|attempt|attempt.source.as_ref()).is_some_and(|source|!source.current(cx)) {
                        form._create=None;
                        if let Some(attempt)=form.attempt.as_mut(){attempt.watch=None;}
                        form.start_error("The captured start connection changed. Reconcile the original request before retrying.",cx);
                    }
                })).collect();
                let args=if lookup{attempt.request.by_client_arguments()}else{attempt.request.arguments()};
                Some(form.client.call("sessions",if lookup{"byClient"}else{"create"},args,None,cx))
            }).ok().flatten();
            let Some(call)=call else{return;};let answer=call.await;
            let _=form.update(cx,|form,cx|form.created_answer(answer,lookup,cx));
        }));
    }
    fn created_answer(&mut self,answer:Result<serde_json::Value,CallError>,lookup:bool,cx:&mut Context<Self>) {
        if self.attempt.as_ref().and_then(|attempt|attempt.source.as_ref()).is_none_or(|source|!source.current(cx)) {
            self.start_error("The create answer belongs to a replaced connection. The original client ID was retained.",cx);return;
        }
        let value=match answer {
            Ok(value)=>value,
            Err(CallError::Aborted|CallError::Timeout(_)|CallError::Uncertain{..}|CallError::Unreachable{..}) if !lookup=>{
                // A lost write reply always gets this indexed read before any explicit same-ID retry.
                self.resolve_start(true,cx);return;
            }
            Err(error)=>{self.start_error(&error.reason(),cx);return;}
        };
        let reply=match serde_json::from_value::<CreateReply>(value) {Ok(reply)=>reply,Err(error)=>{self.start_error(&format!("The start answer is unavailable: {error}"),cx);return;}};
        let requires_checkout=self.attempt.as_ref().is_some_and(|attempt|attempt.request.requires_checkout());
        let accepted=match reply.accepted(false){Ok(accepted)=>accepted,Err(error)=>{self.start_error(&error,cx);return;}};
        let Some(accepted)=accepted else{
            if let Some(attempt)=self.attempt.as_mut(){attempt.lookup_absent=lookup;}
            self.start_error(if lookup{"No accepted session was found. Send explicitly to retry the same request."}else{"The create answer did not identify an accepted session. Reconcile it before retrying."},cx);return;
        };
        if self.attempt.as_ref().and_then(|attempt|attempt.accepted.as_ref()).is_some_and(|previous|previous.session_id!=accepted.session_id) {
            self.start_error("The original client ID returned a different accepted session. The original correlation and draft were retained.",cx);return;
        }
        let attempt=self.attempt.as_mut().expect("owned create");
        let association_matches=accepted.matches_project(attempt.request.project_id());
        attempt.current_operation=accepted.initial_operation_id.clone();attempt.accepted=Some(accepted);attempt.lookup_absent=false;
        if !association_matches {
            self.start_error("The original client ID belongs to a session with a different project association. The accepted session and draft were retained; no new session or prompt was sent.",cx);return;
        }
        if requires_checkout&&self.attempt.as_ref().and_then(|attempt|attempt.accepted.as_ref()).is_some_and(|accepted|accepted.initial_operation_id.is_none()) {
            self.start_error("The accepted session's initial checkout proof is unavailable. The session and draft were retained.",cx);return;
        }
        self.hold_start_scope(cx);
    }
    fn hold_start_scope(&mut self,cx:&mut Context<Self>) {
        let Some(key)=self.start_key()else{return;};let feed=EngineFeed::global(cx);
        let scope=EngineScope{machine:key.machine.clone(),topic:"sessions".into(),key:None};
        let watch=feed.update(cx,|feed,cx|feed.watch(scope,cx));
        let watch=match watch{Ok(watch)=>watch,Err(error)=>{self.start_error(&error.message,cx);return;}};
        let changed=cx.subscribe(&feed,|form,_,event:&EngineChanged,cx|{
            if form.start_key().as_ref().is_some_and(|key|event.sessions.contains(key)||event.scopes.iter().any(|scope|scope.machine==key.machine&&scope.topic=="sessions")) {
                form.observe_start(cx);
            }
        });
        let attempt=self.attempt.as_mut().expect("accepted start");attempt.watch=Some(watch);attempt.changed=Some(changed);
        self.read_start_entity(cx);
    }
    fn read_start_entity(&mut self,cx:&mut Context<Self>) {
        let Some(key)=self.start_key()else{return;};
        if self.attempt.as_ref().and_then(|attempt|attempt.source.as_ref()).is_none_or(|source|!source.current(cx)) {
            self.start_error("The original source connection must be revalidated before reading this start.",cx);return;
        }
        let feed=EngineFeed::global(cx);let task=feed.update(cx,|feed,cx|feed.ensure_item(EngineKey{machine:key.machine,kind:"session".into(),id:key.id},cx));
        self._create=Some(cx.spawn(async move|form,cx| {
            let answer=task.await;
            let _=form.update(cx,|form,cx|match answer {Ok(_)=>form.observe_start(cx),Err(error)=>form.start_error(&error.message,cx)});
        }));
    }
    fn start_key(&self)->Option<SessionKey> {
        let attempt=self.attempt.as_ref()?;Some(attempt.source.as_ref()?.session_key(&attempt.accepted.as_ref()?.session_id))
    }
    fn observe_start(&mut self,cx:&mut Context<Self>) {
        let Some(key)=self.start_key()else{return;};let Some(attempt)=self.attempt.as_ref()else{return;};
        if attempt.source.as_ref().is_none_or(|source|!source.current(cx)){self.start_error("The captured start connection changed.",cx);return;}
        if attempt.accepted.as_ref().is_none_or(|accepted|!accepted.matches_project(attempt.request.project_id())) {
            self.start_error("The accepted session's project differs from this draft. Reconcile that session before sending.",cx);return;
        }
        if attempt.request.requires_checkout()&&attempt.accepted.as_ref().is_some_and(|accepted|accepted.initial_operation_id.is_none()) {
            self.start_error("The accepted session's initial checkout proof is unavailable. Reconcile the original request before sending.",cx);return;
        }
        if attempt.opening.is_some()||attempt.prompt_dispatched{return;}
        let feed=EngineFeed::global(cx);let feed=feed.read(cx);
        if !feed.loaded(&EngineScope{machine:key.machine.clone(),topic:"sessions".into(),key:None}){return;}
        let Some(item)=feed.item(&EngineKey{machine:key.machine.clone(),kind:"session".into(),id:key.id.clone()})else{return;};
        // The accepted retry receipt can arrive before its pushed display row. The old row
        // is not evidence against that exact accepted operation; wait for a newer row.
        if attempt.current_operation.as_deref()!=attempt.accepted.as_ref().and_then(|accepted|accepted.initial_operation_id.as_deref())
            && item.version<=attempt.retry_version {return;}
        let observation=if let Some(operation)=&attempt.retry_operation {
            // This is the actual committed row for our exact command, not a guessed success from display idle.
            if item.version<=attempt.retry_version || item.value["checkoutOperationId"].as_str()!=Some(operation.as_str()) {return;}
            let observation=checkout_wait::observe(&key,attempt.request.project_id(),Some(operation),item);
            if matches!(observation,CheckoutObservation::Changed){return;}
            let attempt=self.attempt.as_mut().expect("same retry");
            attempt.current_operation=attempt.retry_operation.take();attempt.checkout_refused=false;
            observation
        } else {checkout_wait::observe(&key,attempt.request.project_id(),attempt.current_operation.as_deref(),item)};
        match observation {
            CheckoutObservation::Waiting=>{},
            CheckoutObservation::Changed=>self.start_error("The accepted start's project or checkout operation changed. The draft was retained.",cx),
            CheckoutObservation::Refused(reason)=>{
                self.attempt.as_mut().expect("same start").checkout_refused=true;
                self.start_error(reason.as_deref().unwrap_or("Checkout was refused; its detailed reason is unavailable."),cx);
            }
            CheckoutObservation::Ready=>{
                let request_id=uuid::Uuid::new_v4().to_string();
                let attempt=self.attempt.as_mut().expect("same start");attempt.checkout_refused=false;attempt.opening=Some(request_id.clone());
                let window=attempt.window;let form=cx.weak_entity();
                cx.defer(move|cx|{let _=window.update(cx,|_,window,cx|{let _=form.update(cx,|form,cx|form.open_first_prompt(key,request_id,window,cx));});});
            }
        }
    }
    pub(super) fn start_error(&mut self,error:&str,cx:&mut Context<Self>) {
        self.starting=false;self.error=error.to_string().into();
        let requires_reconcile=self.attempt.as_ref().is_some_and(|attempt|attempt.accepted.is_none()&&!attempt.lookup_absent||attempt.prompt_dispatched||attempt.retry_operation.is_some());
        self.start_composer.update(cx,|composer,cx|composer.set_start_state(!requires_reconcile,requires_reconcile,Some(error.into()),cx));
        cx.emit(DraftEvent::Failed);
        cx.notify();
    }
}

#[cfg(test)]
#[path="first_send_tests.rs"]
mod tests;

#[path="first_prompt.rs"]
mod prompt;
#[path="checkout_retry.rs"]
mod retry;

#[cfg(test)]
#[path="first_prompt_wait_tests.rs"]
mod wait_tests;

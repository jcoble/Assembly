//! Request-owned source validation and first-page preparation. Outgoing rows stay visible until workspace commit.
use gpui_kit::*;
use crate::core::{engine::{EngineFeed,EngineKey,EngineRefusal},intents::session_open::{SessionOpenRequest,SessionOpenTarget,SessionOpenResult},socket::client::SocketClient,state::selected_session::SelectedSession};
use crate::features::session::{client::{open_target::{self,OpenTarget},session_client::{CallError,RowUpdate}},model::open_stage::OpenStage,rows::row_page::RowPage};
use super::SessionPane;

pub(super) struct ConnectionCut {
    main:Entity<SocketClient>, bulk:Entity<SocketClient>, main_generation:u64, bulk_generation:u64,local_owner:String,
}
impl ConnectionCut {
    pub(super) fn capture(cx:&App)->Result<Self,EngineRefusal> {
        let main=SocketClient::try_global(cx).ok_or_else(||refused("disconnected","The backend is not connected."))?;
        let bulk=SocketClient::try_bulk(cx).ok_or_else(||refused("disconnected","The bulk connection is not ready."))?;
        let main_generation=main.read(cx).connection_generation().ok_or_else(||refused("disconnected","The backend is not ready."))?;
        let bulk_generation=bulk.read(cx).connection_generation().ok_or_else(||refused("disconnected","The bulk connection is not ready."))?;
        let local_owner=main.read(cx).engine_machine().ok_or_else(||refused("disconnected","The backend has not named its Engine owner."))?;
        Ok(Self{main,bulk,main_generation,bulk_generation,local_owner})
    }
    pub(super) fn local_engine(&self,_:&App)->Option<String>{Some(self.local_owner.clone())}
    pub(super) fn current(&self,cx:&App)->bool {
        SocketClient::try_global(cx).is_some_and(|socket|socket==self.main)
            && SocketClient::try_bulk(cx).is_some_and(|socket|socket==self.bulk)
            && self.main.read(cx).connection_generation()==Some(self.main_generation)
            && self.bulk.read(cx).connection_generation()==Some(self.bulk_generation)
    }
}
pub(super) struct PendingOpen {
    pub checked_engine:Option<crate::core::engine::EngineItem>, pub classify_online:bool,
    pub serial:u64, pub request:Option<SessionOpenRequest>,pub request_id:String,pub discovered_source:Option<crate::core::socket::row_source::ExpectedRowSource>, pub cut:ConnectionCut,
    pub target:Option<OpenTarget>,pub stage:Option<OpenStage>,pub rows:Option<Subscription>,
    pub read:Option<Task<()>>,pub page:Option<Task<()>>,pub connections:Vec<Subscription>,
    pub window:AnyWindowHandle,pub workspace:Option<(u64,crate::features::workspaces::workspace_coordinator::WorkspaceTarget)>,
    pub workspace_queued:bool,pub committed:bool,pub workspace_events:Vec<Subscription>,
}
impl SessionPane {
    fn native_card<'a>(&'a self,key:&str,payload_session_id:&str)->Result<(&'a crate::core::socket::row_source::ExpectedRowSource,&'a crate::features::session::model::session_view::RowReference,&'a crate::features::session::rows::subagent::SubagentPayload),EngineRefusal> {
        let source=self.row_source.as_ref().ok_or_else(||refused("owner_validation_required","The parent session has no captured source."))?;
        let reference=self.view.row_reference(key).ok_or_else(||refused("native_card_changed","The persisted native card reference is unavailable."))?;
        let card=self.view.items.iter().find(|item|item.key==key).and_then(|item|match &item.kind {
            crate::features::session::model::item::ItemKind::Subagent{subagent,..} if !subagent.helper=>Some(subagent),_=>None,
        }).ok_or_else(||refused("native_card_changed","The persisted native card changed."))?;
        if self.session.as_deref()!=Some(reference.session_id.as_str())||card.session_id!=payload_session_id {
            return Err(refused("native_card_changed","The persisted native parent or payload changed."));
        }
        Ok((source,reference,card))
    }
    pub(super) fn native_open_request(&self,key:&str,payload_session_id:&str)->Result<SessionOpenRequest,EngineRefusal> {
        let(source,reference,_)=self.native_card(key,payload_session_id)?;
        let item_id=reference.item_id.clone().ok_or_else(||refused("native_card_changed","The native card has no immutable row item."))?;
        let parent=crate::core::engine::SessionKey{machine:source.machine.clone(),id:reference.session_id.clone()};
        let request=SessionOpenRequest{key:parent.clone(),request_id:uuid::Uuid::new_v4().to_string(),target:SessionOpenTarget::Native{parent,item_id,seq:reference.seq,payload_session_id:payload_session_id.into()}};
        request.validate_shape()?;Ok(request)
    }
    pub(super) fn native_stop_task(&self,key:&str,payload_session_id:&str)->Result<String,EngineRefusal> {
        let(_,reference,card)=self.native_card(key,payload_session_id)?;
        reference.item_id.as_deref().and_then(|item|card.stop_target(item,reference.seq))
            .map(str::to_owned).ok_or_else(||refused("native_card_changed","The native card has no current persisted Stop target."))
    }
    pub fn request_open(&mut self,request:SessionOpenRequest,window:&mut Window,cx:&mut Context<Self>) {
        self.cancel_preparation(refused("cancelled","The open was superseded by a newer request."),cx);
        if let Err(error)=request.validate_shape(){self.open_result(request,Err(error),cx);return;}
        let cut=match ConnectionCut::capture(cx) {Ok(cut)=>cut,Err(error)=>{self.open_result(request,Err(error),cx);return;}};
        let Some(serial)=self.open_serial.checked_add(1) else {self.open_result(request,Err(refused("open_limit","Session open generation exhausted.")),cx);return;};
        self.open_serial=serial;
        let request_id=request.request_id.clone();
        self.install_pending(Some(request),request_id,cut,serial,window,cx);
        self.start_target_read(serial,cx);
    }
    pub(super) fn ready_open(&self,request:&SessionOpenRequest,source:Option<&crate::core::socket::row_source::ExpectedRowSource>,cx:&App)->bool {
        let same_disposition=matches!((&request.target,self.read_only),(SessionOpenTarget::Engine,false)|(SessionOpenTarget::Historical,true));
        same_disposition&&self._rows.is_some()&&self.held.is_none()
            &&self.active_source_cut.as_ref().is_some_and(|cut|cut.current(cx))
            &&SelectedSession::global(cx).read(cx).key()==Some(&request.key)
            &&source.is_none_or(|source|self.row_source.as_ref()==Some(source))
    }
    pub(super) fn install_pending(&mut self,request:Option<SessionOpenRequest>,request_id:String,cut:ConnectionCut,serial:u64,window:&mut Window,cx:&mut Context<Self>) {
        let sockets=[cut.main.clone(),cut.bulk.clone()];
        let connections=sockets.into_iter().map(|socket|cx.subscribe(&socket,move|pane,_,event:&crate::core::socket::connection_state::ConnectionState,cx|{
            if pane.pending_open.as_ref().is_some_and(|pending|pending.serial==serial&&!pending.cut.current(cx)) {
                pane.cancel_preparation(refused("source_changed","The captured session connection changed."),cx);
            }
            if pane.active_open_serial==Some(serial)&&pane.active_source_cut.as_ref().is_some_and(|cut|!cut.current(cx)) {
                pane.on_rows(RowUpdate::Refused{code:"source_changed".into(),message:"The captured session connection changed.".into()},cx);
            }
            if matches!(event,crate::core::socket::connection_state::ConnectionState::Connected{..}) {
                pane.reopen_after_connection(serial,cx);
            }
        })).collect();
        let classify_online=request.as_ref().is_some_and(|request|matches!(&request.target,SessionOpenTarget::Engine));
        self.pending_open=Some(PendingOpen{checked_engine:None,classify_online,serial,request,request_id,discovered_source:None,cut,target:None,stage:None,rows:None,read:None,page:None,connections,window:window.window_handle(),workspace:None,workspace_queued:false,committed:false,workspace_events:Vec::new()});
    }
    pub(super) fn start_target_read(&mut self,serial:u64,cx:&mut Context<Self>) {
        if !self.preparation_current(serial,cx){self.refuse_replaced_preparation(serial,cx);return;}
        let request=self.pending_open.as_ref().and_then(|pending|pending.request.clone()).expect("resolved request");
        let ensure=matches!(request.target,SessionOpenTarget::Engine).then(|| {
            let feed=EngineFeed::global(cx);
            feed.update(cx,|feed,cx|feed.ensure_item(EngineKey{machine:request.key.machine.clone(),kind:"session".into(),id:request.key.id.clone()},cx))
        });
        let task=cx.spawn(async move |pane,cx| {
            if let Some(ensure)=ensure {
                let result=ensure.await;
                let proceed=pane.update(cx,|pane,cx| {
                    if !pane.preparation_current(serial,cx){pane.refuse_replaced_preparation(serial,cx);return false;}
                    match result {
                        Ok(Some(item))=>{
                            let Some(display)=crate::core::engine::engine_items::SessionItem::of(&item) else {
                                pane.cancel_preparation(refused("invalid_engine_session","The Engine session display is incomplete."),cx);return false;
                            };
                            let pending=pane.pending_open.as_mut().expect("same preparation");
                            if pending.classify_online && online_historical(&display.state) {
                                pending.request.as_mut().expect("resolved request").target=SessionOpenTarget::Historical;
                            }
                            pending.checked_engine=Some(item);true
                        },
                        Ok(None)=>{pane.cancel_preparation(refused("unknown_engine_session","The Engine session is unavailable."),cx);false},
                        Err(error)=>{pane.cancel_preparation(error,cx);false},
                    }
                }).unwrap_or(false);
                if !proceed{return;}
            }
            let call=pane.update(cx,|pane,cx| {
                if !pane.preparation_current(serial,cx) {return None;}
                let request=pane.pending_open.as_ref()?.request.as_ref()?;
                match open_target::build_args(request,serial) {
                    Ok(args)=>Some(pane.client.call("sessions","open_target",args,None,cx)),
                    Err(error)=>{pane.cancel_preparation(error,cx);None},
                }
            }).ok().flatten();
            let Some(call)=call else{return;};
            let answer=call.await;
            let _=pane.update(cx,|pane,cx|pane.target_read(serial,answer,cx));
        });
        self.pending_open.as_mut().expect("just installed request").read=Some(task);
    }
    pub(super) fn preparation_current(&self,serial:u64,cx:&App)->bool {
        self.pending_open.as_ref().is_some_and(|pending|pending.serial==serial&&pending.cut.current(cx))
    }
    fn target_read(&mut self,serial:u64,answer:Result<serde_json::Value,CallError>,cx:&mut Context<Self>) {
        if !self.preparation_current(serial,cx){self.refuse_replaced_preparation(serial,cx);return;}
        let pending=self.pending_open.as_ref().expect("current request");
        let target=answer.map_err(call_refusal).and_then(|value|open_target::parse_response(value,pending.request.as_ref().expect("resolved request"),serial));
        let target=match target{Ok(target)=>target,Err(error)=>{self.cancel_preparation(error,cx);return;}};
        if pending.discovered_source.as_ref().is_some_and(|source|source!=&target.source) {
            self.cancel_preparation(refused("source_changed","The discovered source changed before target validation."),cx);return;
        }
        let request=pending.request.as_ref().expect("resolved request").clone();
        if self.ready_open(&request,Some(&target.source),cx) && target.key==request.key && target.read_only==self.read_only {
            self.pending_open=None;self.open_result(request,Ok(()),cx);return;
        }
        let session=target.key.id.clone();let source=target.source.clone();
        let pending=self.pending_open.as_mut().expect("current request");
        pending.stage=Some(OpenStage::new(session.clone()));pending.target=Some(target);
        let pane=cx.weak_entity();
        let rows=self.client.subscribe_rows(&session,None,Some(&source),Box::new(move|event,cx| {
            let _=pane.update(cx,|pane,cx|pane.preparing_rows(serial,event,cx));
        }),cx);
        if let Some(pending)=self.pending_open.as_mut().filter(|pending|pending.serial==serial){pending.rows=Some(rows);}
    }
    fn preparing_rows(&mut self,serial:u64,event:RowUpdate,cx:&mut Context<Self>) {
        if self.active_open_serial==Some(serial) {self.on_rows(event,cx);return;}
        if !self.preparation_current(serial,cx){self.refuse_replaced_preparation(serial,cx);return;}
        match event {
            RowUpdate::Refused{code,message}=>self.cancel_preparation(EngineRefusal{code,message},cx),
            RowUpdate::Row(row)=>{
                let result=self.pending_open.as_mut().and_then(|pending|pending.stage.as_mut()).expect("target installed").push(*row);
                if let Err(error)=result {self.cancel_preparation(refused("source_changed",&error),cx);}else{self.workspace_if_ready(serial,cx);cx.notify();}
            }
            RowUpdate::Jump{seq}=>{
                let pending=self.pending_open.as_mut().expect("current request");pending.page=None;
                let ticket=match pending.stage.as_mut().expect("target installed").jump(seq){Ok(ticket)=>ticket,Err(error)=>{self.cancel_preparation(refused("invalid_row_cut",&error),cx);return;}};
                let target=pending.target.as_ref().expect("target installed");
                let args=crate::core::socket::row_source::page_args(&target.key.id,ticket.before,100,Some(&target.source));
                let key=format!("session-open:{serial}");
                let call=self.client.call("rows","page",args,Some(&key),cx);
                let page=cx.spawn(async move|pane,cx| {
                    let answer=call.await.map_err(call_refusal).and_then(|value|serde_json::from_value::<RowPage>(value).map_err(|error|refused("invalid_row_page",&error.to_string())));
                    let _=pane.update(cx,|pane,cx|{
                        if !pane.preparation_current(serial,cx){pane.refuse_replaced_preparation(serial,cx);return;}
                        let result=answer.and_then(|page|pane.pending_open.as_mut().and_then(|pending|pending.stage.as_mut()).expect("target installed").install(ticket,page).map_err(|error|refused("invalid_row_page",&error)));
                        if let Err(error)=result{pane.cancel_preparation(error,cx);}else{pane.workspace_if_ready(serial,cx);cx.notify();}
                    });
                });
                self.pending_open.as_mut().expect("current request").page=Some(page);
            }
        }
    }
    pub(super) fn refuse_replaced_preparation(&mut self,serial:u64,cx:&mut Context<Self>) {
        if self.pending_open.as_ref().is_some_and(|pending|pending.serial==serial) {
            self.cancel_preparation(refused("source_changed","The captured session connection changed."),cx);
        }
    }
    pub(super) fn cancel_preparation(&mut self,error:EngineRefusal,cx:&mut Context<Self>) {
        let Some(mut pending)=self.pending_open.take()else{return;};
        if let Some(stage)=pending.stage.as_mut(){stage.invalidate();}
        if pending.committed {
            self.composer.update(cx,|composer,cx|composer.invalidate_source(cx));
            self._rows=None;self._page=None;self._loads.clear();self.status=error.message.clone().into();
        }
        if let Some((generation,_))=pending.workspace.take() {
            if let Some(coordinator)=crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator::try_global(cx) {
                coordinator.update(cx,|coordinator,cx|coordinator.cancel(generation,cx));
            }
        }
        if let Some(request)=pending.request {self.open_result(request,Err(error),cx);}
        else {self.unknown_open_result(pending.request_id,error,cx);}
        cx.notify();
    }
    pub(super) fn unknown_open_result(&mut self,request_id:String,error:EngineRefusal,cx:&mut Context<Self>){
        SelectedSession::global(cx).update(cx,|_,cx|cx.emit(SessionOpenResult{request_id,key:None,result:Err(error)}));
    }
    pub(super) fn open_result(&mut self,request:SessionOpenRequest,result:Result<(),EngineRefusal>,cx:&mut Context<Self>) {
        SelectedSession::global(cx).update(cx,|_,cx|cx.emit(SessionOpenResult{request_id:request.request_id,key:Some(request.key),result}));
    }
}
pub(super) fn refused(code:&str,message:&str)->EngineRefusal{EngineRefusal{code:code.into(),message:message.into()}}
pub(super) fn call_refusal(error:CallError)->EngineRefusal {
    match error {
        CallError::Refused{code,message}=>EngineRefusal{code,message},
        error=>refused("session_open_failed",&error.reason()),
    }
}

pub(super) fn online_historical(state:&str)->bool {matches!(state,"ended"|"lost"|"moved"|"handing_off")}

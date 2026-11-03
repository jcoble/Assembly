//! Persisted native display uses one checked parent descriptor and the sole shared rows cache.
use std::rc::Rc;
use gpui_kit::*;
use gpui_kit::component::{ActiveTheme as _,v_flex};
use gpui_kit::component::button::{Button,ButtonVariants as _};
use serde_json::{Value,json};
use crate::core::{engine::{EngineFeed,EngineChanged,EngineKey,EngineScope,SessionKey,NativeWatch,SubagentItem,engine_items::SessionItem},socket::{client::SocketClient,outcome::Outcome,row_source::ExpectedRowSource},intents::session_open::{SessionOpenRequest,SessionOpenTarget}};
use crate::features::session::{client::{source_resolution,open_target},rows::subagent::SubagentPayload,view::{subagent_card::SubagentCard,session_action::SessionAction}};
#[derive(Clone)]
struct NativeCut {main:Entity<SocketClient>,bulk:Entity<SocketClient>,main_generation:u64,bulk_generation:u64,owner:String}
impl NativeCut {
    fn capture(cx:&App)->Option<Self> {
        let main=SocketClient::try_global(cx)?;let bulk=SocketClient::try_bulk(cx)?;
        let main_generation=main.read(cx).connection_generation()?;let bulk_generation=bulk.read(cx).connection_generation()?;
        let owner=main.read(cx).engine_machine()?;Some(Self{main,bulk,main_generation,bulk_generation,owner})
    }
    fn current(&self,cx:&App)->bool {
        SocketClient::try_global(cx).as_ref()==Some(&self.main)&&SocketClient::try_bulk(cx).as_ref()==Some(&self.bulk)
            &&self.main.read(cx).connection_generation()==Some(self.main_generation)&&self.bulk.read(cx).connection_generation()==Some(self.bulk_generation)
    }
}
pub struct NativeDetails {
    parent:SessionKey,route:Option<String>,feed:Entity<EngineFeed>,cut:Option<NativeCut>,source:Option<ExpectedRowSource>,
    watch:Option<NativeWatch>,items:Rc<Vec<SubagentItem>>,dirty:bool,allow_stop:bool,error:Option<String>,serial:u64,
    read:Option<Task<()>>,stopping:Option<Task<()>>,_changed:Subscription,_connections:Vec<Subscription>,
}
impl NativeDetails {
    pub fn new(parent:SessionKey,route:Option<String>,cx:&mut Context<Self>)->Self {
        let feed=EngineFeed::global(cx);
        let changed=cx.subscribe(&feed,|view,_,event:&EngineChanged,cx|{
            if event.sessions.contains(&view.parent){view.dirty=true;cx.notify();}
        });
        let mut view=Self{parent,route,feed,cut:None,source:None,watch:None,items:Rc::default(),dirty:true,allow_stop:false,error:None,serial:0,read:None,stopping:None,_changed:changed,_connections:Vec::new()};
        view.start(cx);view
    }
    fn start(&mut self,cx:&mut Context<Self>) {
        self.read=None;self.stopping=None;self.watch=None;self.source=None;self.allow_stop=false;self.items=Rc::default();self._connections.clear();self.error=None;
        let Some(serial)=self.serial.checked_add(1)else{self.fail("Native display request limit reached.".into(),cx);return;};self.serial=serial;
        let Some(cut)=NativeCut::capture(cx)else{self.fail("The owning source connections are not ready.".into(),cx);return;};
        self.cut=Some(cut.clone());
        for socket in [&cut.main,&cut.bulk] {
            self._connections.push(cx.subscribe(socket,|view,_,_:&crate::core::socket::connection_state::ConnectionState,cx|{
                if view.cut.as_ref().is_some_and(|cut|!cut.current(cx)) {
                    view.read=None;view.stopping=None;view.items=Rc::default();
                    view.fail("The native parent source changed. Revalidate it to continue.".into(),cx);
                }
            }));
        }
        let args=match source_resolution::build_args(self.route.as_deref()){Ok(args)=>args,Err(error)=>{self.fail(error.message,cx);return;}};
        let call=cut.main.read(cx).call::<Value>("sessions","resolve_source",args,None);
        let parent=self.parent.clone();let route=self.route.clone();
        let scope=EngineScope{machine:parent.machine.clone(),topic:"sessions".into(),key:None};
        let operational=self.feed.read(cx).loaded(&scope)&&self.feed.read(cx).item(&EngineKey{machine:parent.machine.clone(),kind:"session".into(),id:parent.id.clone()}).and_then(SessionItem::of).is_some_and(|item|!matches!(item.state.as_str(),"ended"|"lost"|"moved"|"handing_off"|"foreign"));
        let request=SessionOpenRequest{key:parent.clone(),request_id:uuid::Uuid::new_v4().to_string(),target:if operational{SessionOpenTarget::Engine}else{SessionOpenTarget::Historical}};
        self.read=Some(cx.spawn(async move|view,cx|{
            let resolved=match call.await {
                Outcome::Ok(value)=>source_resolution::parse_reply(value,route.as_deref(),Some(&cut.owner)).map_err(|error|error.message),
                Outcome::Failed(error)=>Err(error.message),Outcome::Aborted=>Err("The native parent source read was cancelled.".into()),
            };
            let source=match resolved {
                Ok(source) if source.machine==parent.machine=>source,
                Ok(_)=>{let _=view.update(cx,|view,cx|{if view.serial==serial{view.fail("The saved route now names a different Engine owner.".into(),cx);}});return;},
                Err(error)=>{let _=view.update(cx,|view,cx|{if view.serial==serial{view.fail(error,cx);}});return;},
            };
            let call=view.update(cx,|view,cx|{
                if view.serial!=serial{return None;}
                if !cut.current(cx){view.fail("The native parent connection changed.".into(),cx);return None;}
                match open_target::build_args(&request,serial) {Ok(args)=>Some(cut.main.read(cx).call::<Value>("sessions","open_target",args,None)),Err(error)=>{view.fail(error.message,cx);None}}
            }).ok().flatten();let Some(call)=call else{return;};
            let target=match call.await {Outcome::Ok(value)=>open_target::parse_response(value,&request,serial).map_err(|error|error.message),Outcome::Failed(error)=>Err(error.message),Outcome::Aborted=>Err("The native parent descriptor was cancelled.".into())};
            let _=view.update(cx,|view,cx|{
                if view.serial!=serial{return;}
                let target=match target{Ok(target)=>target,Err(error)=>{view.fail(error,cx);return;}};
                if !cut.current(cx)||target.key!=parent||target.source!=source {view.fail("The native parent source changed before watch admission.".into(),cx);return;}
                match view.feed.update(cx,|feed,cx|feed.watch_native(parent,source.clone(),cx)) {
                    Ok(watch)=>{view.allow_stop=!target.read_only;view.source=Some(source);view.watch=Some(watch);view.dirty=true;view.error=None;cx.notify();},
                    Err(error)=>view.fail(error.message,cx),
                }
            });
        }));
    }
    fn fail(&mut self,error:String,cx:&mut Context<Self>){self.error=Some(error);self.watch=None;self.source=None;self.allow_stop=false;cx.notify();}
    fn refresh(&mut self,cx:&mut Context<Self>) {
        if !self.dirty{return;}self.dirty=false;
        self.items=Rc::new(self.feed.read(cx).subagents_of(&self.parent));
        if let Some(error)=self.watch.as_ref().and_then(NativeWatch::error){self.error=Some(error.message);self.allow_stop=false;}
    }
    fn card(&self,item:&str,seq:i64,payload:&str,cx:&App)->Option<SubagentPayload> {
        if !self.cut.as_ref()?.current(cx)||!self.watch.as_ref()?.loaded()||self.source.as_ref()?.machine!=self.parent.machine{return None;}
        let shown=self.feed.read(cx).subagents_of(&self.parent).into_iter().find(|shown|shown.item_id==item&&shown.seq==seq&&shown.session_id==payload)?;
        Some(payload_of(&shown))
    }
    fn open(&mut self,item:&str,seq:i64,payload:&str,window:&mut Window,cx:&mut Context<Self>) {
        if self.card(item,seq,payload,cx).is_none(){self.error=Some("The persisted native card changed. Review its current display.".into());cx.notify();return;}
        window.dispatch_action(Box::new(SessionOpenRequest{key:self.parent.clone(),request_id:uuid::Uuid::new_v4().to_string(),target:SessionOpenTarget::Native{parent:self.parent.clone(),item_id:item.into(),seq,payload_session_id:payload.into()}}),cx);
    }
    fn stop(&mut self,item:&str,seq:i64,payload:&str,cx:&mut Context<Self>) {
        if !self.allow_stop||self.stopping.is_some(){return;}
        let Some(card)=self.card(item,seq,payload,cx)else{self.error=Some("The persisted native card changed.".into());cx.notify();return;};
        let Some(task)=card.stop_target(item,seq)else{return;};
        let Some(source)=self.source.clone()else{return;};let Some(cut)=self.cut.clone().filter(|cut|cut.current(cx))else{return;};let serial=self.serial;
        let call=cut.main.read(cx).call::<Value>("send","stopTask",json!({"session":self.parent.id,"task":task,"expectedSource":source}),None);
        self.stopping=Some(cx.spawn(async move|view,cx|{
            let result=call.await;let _=view.update(cx,|view,cx|{
                if view.serial!=serial{return;}view.stopping=None;
                match result {Outcome::Ok(_)=>{},Outcome::Failed(error)=>{
                    if matches!(&error.code,crate::core::socket::error_code::ErrorCode::Refused{code} if code=="source_changed") {view.fail(error.message,cx);}
                    else{view.error=Some(error.message);}
                },Outcome::Aborted=>view.error=Some("The Stop outcome is unknown. Review the current native state before another action.".into())};cx.notify();
            });
        }));cx.notify();
    }
}
fn payload_of(item:&SubagentItem)->SubagentPayload {
    SubagentPayload{session_id:item.session_id.clone(),title:item.title.clone(),description:item.description.clone(),state:item.state.clone(),stop_reason:item.stop_reason.clone(),error:item.error.clone(),can_stop:item.can_stop,tool_call_id:item.tool_call_id.clone(),helper:false}
}
impl Render for NativeDetails {
    fn render(&mut self,_:&mut Window,cx:&mut Context<Self>)->impl IntoElement {
        self.refresh(cx);let mut column=v_flex().gap_1();
        if let Some(error)=&self.error {
            column=column.child(div().text_color(cx.theme().muted_foreground).child(error.clone())).child(Button::new("retry-native-source").ghost().label("Revalidate native activity").on_click(cx.listener(|view,_,_,cx|view.start(cx))));
            if !self.watch.as_ref().is_some_and(NativeWatch::loaded){return column;}
        }
        if !self.watch.as_ref().is_some_and(NativeWatch::loaded){return column.child("Reading persisted native activity…");}
        if self.items.is_empty(){return column;}
        column=column.child(div().text_color(cx.theme().muted_foreground).child("Native activity"));
        for item in self.items.iter() {
            let mut payload=payload_of(item);payload.can_stop&=self.allow_stop&&self.stopping.is_none()&&self.error.is_none();
            let seq=item.seq;let weak=cx.weak_entity();
            column=column.child(SubagentCard{key:item.item_id.clone(),subagent:payload,on_action:Rc::new(move|action,window,cx|{
                let _=weak.update(cx,|view,cx|match action {
                    SessionAction::OpenNative{key,payload_session_id}=>view.open(&key,seq,&payload_session_id,window,cx),
                    SessionAction::StopNative{key,payload_session_id}=>{view.stop(&key,seq,&payload_session_id,cx);cx.stop_propagation();},_=>{},
                });
            })});
        }
        column
    }
}
#[cfg(test)]
#[path="native_details_tests.rs"]
mod tests;

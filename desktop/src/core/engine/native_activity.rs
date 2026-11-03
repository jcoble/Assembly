//! Shared persisted native display state over the existing parent rows watch and bounded SQL projection.
//! Neither the payload SessionId nor this cache creates an Engine/native execution identity.
use std::{cell::{Cell,RefCell},collections::HashSet,rc::{Rc,Weak}};
use gpui_kit::*;
use serde::{Deserialize,Serialize};
use serde_json::{Value,json};
use super::{EngineFeed,EngineChanged,EngineRefusal,SessionKey};
use crate::core::socket::{client::SocketClient,connection_state::ConnectionState,outcome::Outcome,row_source::ExpectedRowSource,row_update::RowUpdate};

#[derive(Clone,Debug,PartialEq,Serialize,Deserialize)]
#[serde(rename_all="camelCase",deny_unknown_fields)]
pub struct SubagentItem {
    pub item_id:String,pub seq:i64,pub session_id:String,pub title:String,pub description:Option<String>,
    pub state:String,pub stop_reason:Option<String>,pub error:Option<String>,pub tool_call_id:Option<String>,
    #[serde(default)]
    pub can_stop: bool,
}
#[derive(Deserialize)]
#[serde(rename_all="camelCase",deny_unknown_fields)]
pub(super) struct NativePage {pub items:Vec<SubagentItem>,pub next:Option<String>}
#[derive(Default)]
pub(super) struct NativeState {
    pub items:Vec<SubagentItem>,through:Option<i64>,revision:u64,reading:bool,dirty:bool,coherent:bool,error:Option<EngineRefusal>,
}
impl NativeState {
    pub fn install(&mut self,mut items:Vec<SubagentItem>)->Result<(),EngineRefusal> {
        let mut ids=HashSet::new();
        for item in &items {
            if item.item_id.is_empty()||item.seq<=0||!ids.insert(item.item_id.clone()) {return Err(failed("Invalid or duplicate persisted native display identity."));}
        }
        let held:std::collections::HashMap<_,_>=self.items.iter().map(|item|(&item.item_id,item)).collect();
        for item in &mut items {
            if let Some(previous)=held.get(&item.item_id).filter(|previous|previous.seq>item.seq){*item=(**previous).clone();}
        }
        self.items=items;self.coherent=true;self.error=None;Ok(())
    }
}
pub(super) struct NativeScope {
    parent:SessionKey,source:ExpectedRowSource,active:Cell<bool>,state:RefCell<NativeState>,
    rows:RefCell<Option<Task<()>>>,reader:RefCell<Option<Task<()>>>,connections:RefCell<Vec<Subscription>>,
}
impl Drop for NativeScope {
    fn drop(&mut self){self.active.set(false);self.reader.get_mut().take();self.rows.get_mut().take();}
}
#[derive(Clone)]
pub struct NativeWatch {scope:Rc<NativeScope>}
impl NativeWatch {
    pub fn loaded(&self)->bool {self.scope.active.get()&&self.scope.state.borrow().coherent}
    pub fn error(&self)->Option<EngineRefusal>{self.scope.state.borrow().error.clone()}
}
impl EngineFeed {
    /// Source is the actual checked descriptor assertion, never a saved-name or local-generation fallback.
    pub fn watch_native(&mut self,parent:SessionKey,source:ExpectedRowSource,cx:&mut Context<Self>)->Result<NativeWatch,EngineRefusal> {
        if parent.id.is_empty()||parent.machine.is_empty()||source.machine!=parent.machine
            ||(source.generation==0&&source.route_machine.is_some())
            ||(source.generation>0&&source.route_machine.as_ref().is_none_or(|route|route.trim().is_empty())) {
            return Err(changed("The native parent has no matching captured source."));
        }
        self.native_watches.retain(|_,scope|scope.strong_count()>0);
        if let Some(scope)=self.native_watches.get(&parent).and_then(Weak::upgrade).filter(|scope|scope.active.get()) {
            if scope.source!=source{return Err(changed("The existing native watch belongs to another source generation."));}
            if scope.state.borrow().error.is_some(){Self::start_native_read(&scope,cx);}
            return Ok(NativeWatch{scope});
        }
        let main=SocketClient::try_global(cx).ok_or_else(||failed("The backend is not connected."))?;
        let bulk=SocketClient::try_bulk(cx).ok_or_else(||failed("The bulk connection is not available."))?;
        let mut rows=main.read(cx).subscribe_rows(&parent.id,None,Some(&source));
        let scope=Rc::new(NativeScope{parent:parent.clone(),source,active:Cell::new(true),state:RefCell::new(NativeState::default()),rows:RefCell::new(None),reader:RefCell::new(None),connections:RefCell::new(Vec::new())});
        let weak=Rc::downgrade(&scope);
        let watch=cx.spawn(async move|feed,cx|{
            while let Ok(update)=rows.updates.recv().await {
                let weak=weak.clone();
                if feed.update(cx,|feed,cx| {
                    let Some(scope)=weak.upgrade().filter(|scope|scope.active.get())else{return false;};
                    feed.native_update(&scope,update,cx);scope.active.get()
                }).unwrap_or(false)==false{return;}
            }
            let _=feed.update(cx,|feed,cx|{if let Some(scope)=weak.upgrade(){feed.stop_native(&scope,failed("The native parent row stream ended."),cx);}});
        });
        *scope.rows.borrow_mut()=Some(watch);
        for(socket,is_main)in[(main,true),(bulk,false)] {
            let weak=Rc::downgrade(&scope);
            let heard=cx.subscribe(&socket,move|feed,_,event:&ConnectionState,cx| {
                let Some(scope)=weak.upgrade().filter(|scope|scope.active.get())else{return;};
                if is_main||!matches!(event,ConnectionState::Connected { .. }){
                    scope.reader.borrow_mut().take();let mut state=scope.state.borrow_mut();state.revision=state.revision.wrapping_add(1);state.reading=false;state.dirty=true;state.coherent=false;
                    if is_main{state.through=None;}
                }
                if matches!(event,ConnectionState::Connected { .. }){Self::start_native_read(&scope,cx);}
                feed.native_changed(&scope.parent,cx);
            });
            scope.connections.borrow_mut().push(heard);
        }
        self.native_watches.insert(parent,Rc::downgrade(&scope));Ok(NativeWatch{scope})
    }
    pub fn subagents_of(&self,parent:&SessionKey)->Vec<SubagentItem> {
        self.native_watches.get(parent).and_then(Weak::upgrade).filter(|scope|scope.active.get())
            .map(|scope|scope.state.borrow().items.clone()).unwrap_or_default()
    }
    fn native_update(&mut self,scope:&Rc<NativeScope>,update:RowUpdate,cx:&mut Context<Self>) {
        match update {
            RowUpdate::Refused{code,message}=>self.stop_native(scope,EngineRefusal{code,message},cx),
            RowUpdate::Jump{seq}=>{
                if seq<0{self.stop_native(scope,failed("The native parent row cut is invalid."),cx);return;}
                scope.reader.borrow_mut().take();let mut state=scope.state.borrow_mut();
                state.revision=state.revision.wrapping_add(1);state.through=Some(seq);state.reading=false;state.dirty=true;state.coherent=false;
                drop(state);Self::start_native_read(scope,cx);self.native_changed(&scope.parent,cx);
            }
            RowUpdate::Row(row)=>{
                if row.session_id!=scope.parent.id{self.stop_native(scope,changed("The native parent row identity changed."),cx);return;}
                let mut state=scope.state.borrow_mut();state.through=Some(row.seq);
                if row.kind=="subagent"{state.dirty=true;}else{return;}
                drop(state);Self::start_native_read(scope,cx);
            }
        }
    }
    fn start_native_read(scope:&Rc<NativeScope>,cx:&mut Context<Self>) {
        let mut state=scope.state.borrow_mut();
        if !scope.active.get()||state.reading||state.through.is_none(){return;}
        let Some(socket)=SocketClient::try_bulk(cx)else{state.error=Some(failed("The bulk connection is unavailable."));return;};
        let Some(generation)=socket.read(cx).connection_generation()else{return;};
        state.reading=true;state.dirty=false;let revision=state.revision;
        let parent=scope.parent.clone();let source=scope.source.clone();let weak=Rc::downgrade(scope);drop(state);
        let read=cx.spawn(async move|feed,cx| {
            let result=read_pages(&parent,&source,socket,generation,cx).await;
            let _=feed.update(cx,|feed,cx|{
                let Some(scope)=weak.upgrade().filter(|scope|scope.active.get())else{return;};
                if !feed.native_watches.get(&parent).and_then(Weak::upgrade).is_some_and(|current|Rc::ptr_eq(&current,&scope)){return;}
                let mut state=scope.state.borrow_mut();if state.revision!=revision{return;}
                state.reading=false;
                let terminal=match result {
                    Ok(items)=>{if let Err(error)=state.install(items){state.error=Some(error);state.coherent=false;}false},
                    Err(error)=>{let terminal=error.code=="source_changed";state.error=Some(error);state.coherent=false;terminal},
                };
                let dirty=state.dirty;let error=state.error.clone();drop(state);
                scope.reader.borrow_mut().take();
                if terminal{feed.stop_native(&scope,error.expect("terminal error"),cx);}else{
                    feed.native_changed(&parent,cx);if dirty{Self::start_native_read(&scope,cx);}
                }
            });
        });
        *scope.reader.borrow_mut()=Some(read);
    }
    fn stop_native(&mut self,scope:&Rc<NativeScope>,error:EngineRefusal,cx:&mut Context<Self>) {
        if !scope.active.replace(false){return;}
        let mut state=scope.state.borrow_mut();state.items.clear();state.coherent=false;state.error=Some(error);drop(state);
        scope.reader.borrow_mut().take();scope.rows.borrow_mut().take();scope.connections.borrow_mut().clear();
        if self.native_watches.get(&scope.parent).and_then(Weak::upgrade).is_some_and(|current|Rc::ptr_eq(&current,scope)){self.native_changed(&scope.parent,cx);}
    }
    fn native_changed(&mut self,parent:&SessionKey,cx:&mut Context<Self>){cx.emit(EngineChanged{scopes:Vec::new(),keys:Vec::new(),sessions:vec![parent.clone()]});cx.notify();}
}
pub(super) fn decode_page(value:Value)->Result<NativePage,EngineRefusal> {
    if serde_json::to_vec(&value).map_err(|_|failed("Invalid native display JSON."))?.len()>1024*1024{return Err(failed("The native display page exceeds 1 MiB."));}
    let raw=value.get("items").and_then(Value::as_array).ok_or_else(||failed("The native display page has no items."))?;
    if raw.len()>200||raw.iter().any(|item|serde_json::to_vec(item).map_or(true,|bytes|bytes.len()>32*1024)){return Err(failed("The native display page exceeds its item limit."));}
    serde_json::from_value(value).map_err(|error|failed(&error.to_string()))
}
async fn read_pages(parent:&SessionKey,source:&ExpectedRowSource,socket:Entity<SocketClient>,generation:u64,cx:&mut AsyncApp)->Result<Vec<SubagentItem>,EngineRefusal> {
    let mut items=Vec::new();let mut after:Option<String>=None;let mut seen=HashSet::new();let mut cursors=HashSet::new();
    loop {
        let call=cx.update(|cx| {
            if SocketClient::try_bulk(cx).is_none_or(|current|current!=socket)||socket.read(cx).connection_generation()!=Some(generation){return Err(changed("The captured native bulk connection changed."));}
            Ok(socket.read(cx).call::<Value>("rows","subagents",json!({"session":parent.id,"after":after,"limit":200,"expectedSource":source}),None))
        })?;
        let value=match call.await{Outcome::Ok(value)=>value,Outcome::Aborted=>return Err(changed("The native display read was cancelled.")),Outcome::Failed(error)=>{
            let code=match error.code{crate::core::socket::error_code::ErrorCode::Refused{code}=>code,_=>"native_read_failed".into()};return Err(EngineRefusal{code,message:error.message});
        }};
        if cx.update(|cx|SocketClient::try_bulk(cx).is_none_or(|current|current!=socket)||socket.read(cx).connection_generation()!=Some(generation)){return Err(changed("The captured native bulk connection changed."));}
        let page=decode_page(value)?;
        for item in &page.items {
            if !seen.insert(item.item_id.clone()){return Err(failed("The native item continuation repeated a display identity."));}
        }
        match page.next {
            Some(next)if !page.items.is_empty()&&!next.is_empty()&&cursors.insert(next.clone())=>{items.extend(page.items);after=Some(next);},
            Some(_)=>return Err(failed("The native item continuation made no progress.")),
            None=>{items.extend(page.items);return Ok(items);}
        }
    }
}
fn failed(message:&str)->EngineRefusal{EngineRefusal{code:"native_read_failed".into(),message:message.into()}}
fn changed(message:&str)->EngineRefusal{EngineRefusal{code:"source_changed".into(),message:message.into()}}

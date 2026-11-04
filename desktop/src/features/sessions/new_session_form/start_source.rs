use crate::core::{engine::SessionKey,socket::{client::SocketClient,outcome::Outcome,row_source::ExpectedRowSource}};
use crate::features::session::client::source_resolution;
use gpui_kit::*;
use serde_json::Value;

/// One start attempt's current-source discovery and the exact two socket lifetimes it used.
pub struct StartSource {
    pub source:ExpectedRowSource,
    main:Entity<SocketClient>,bulk:Entity<SocketClient>,
    main_generation:u64,bulk_generation:u64,local_owner:String,
}
impl StartSource {
    pub fn resolve(route:Option<String>,cx:&mut App)->Task<Result<Self,String>> {
        let args=match source_resolution::build_args(route.as_deref()){Ok(args)=>args,Err(error)=>return Task::ready(Err(error.message))};
        let (main,bulk,main_generation,bulk_generation,local_owner)=match capture(cx){Ok(cut)=>cut,Err(error)=>return Task::ready(Err(error))};
        let call=main.read(cx).call::<Value>("sessions","resolve_source",args,None);
        cx.spawn(async move|cx| {
            let value=match call.await {Outcome::Ok(value)=>value,Outcome::Failed(error)=>return Err(error.message),Outcome::Aborted=>return Err("The source discovery outcome is unavailable.".into())};
            let source=source_resolution::parse_reply(value,route.as_deref(),Some(&local_owner)).map_err(|error|error.message)?;
            let captured=Self{source,main,bulk,main_generation,bulk_generation,local_owner};
            if !cx.update(|cx|captured.current(cx)){return Err("The start source connection changed. The draft was retained.".into());}
            Ok(captured)
        })
    }
    pub fn current(&self,cx:&App)->bool {
        SocketClient::try_global(cx).as_ref()==Some(&self.main)&&SocketClient::try_bulk(cx).as_ref()==Some(&self.bulk)
            &&self.main.read(cx).connection_generation()==Some(self.main_generation)&&self.bulk.read(cx).connection_generation()==Some(self.bulk_generation)
            &&self.main.read(cx).engine_machine().as_deref()==Some(self.local_owner.as_str())&&self.bulk.read(cx).engine_machine().as_deref()==Some(self.local_owner.as_str())
    }
    pub fn sockets(&self)->[Entity<SocketClient>;2] {[self.main.clone(),self.bulk.clone()]}
    pub fn session_key(&self,id:&str)->SessionKey {SessionKey{machine:self.source.machine.clone(),id:id.into()}}
}
fn capture(cx:&App)->Result<(Entity<SocketClient>,Entity<SocketClient>,u64,u64,String),String> {
    let main=SocketClient::try_global(cx).ok_or("The backend is not connected.")?;
    let bulk=SocketClient::try_bulk(cx).ok_or("The bulk connection is unavailable.")?;
    let main_generation=main.read(cx).connection_generation().ok_or("The backend is not ready.")?;
    let bulk_generation=bulk.read(cx).connection_generation().ok_or("The bulk connection is not ready.")?;
    let local_owner=main.read(cx).engine_machine().ok_or("The backend has not named its owning Engine.")?;
    if bulk.read(cx).engine_machine().as_deref()!=Some(local_owner.as_str()){return Err("The source connections have different owners.".into());}
    Ok((main,bulk,main_generation,bulk_generation,local_owner))
}
#[cfg(test)]
#[path="start_source_tests.rs"]
mod tests;

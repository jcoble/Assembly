//! Bounded sequential reads on one captured bulk connection. Cancellation drops the active call.
use gpui_kit::*;
use serde_json::{Value,json};
use super::{EngineRefusal,workflow_items::{RunPages,RunView}};
use crate::core::socket::{client::SocketClient,outcome::Outcome};
pub async fn run(machine:&str,run_id:&str,cx:&mut AsyncApp)->Result<RunView,EngineRefusal> {
    let (socket,generation)=cx.update(|cx| {
        let socket=SocketClient::try_bulk(cx).ok_or_else(||refused("The workflow bulk connection is unavailable."))?;
        let generation=socket.read(cx).connection_generation().ok_or_else(||refused("The workflow connection is not ready."))?;
        Ok::<_,EngineRefusal>((socket,generation))
    })?;
    let mut pages:Option<RunPages>=None;
    loop {
        let after=pages.as_ref().and_then(|pages|pages.view().next.clone());
        let call=cx.update(|cx| {
            if SocketClient::try_bulk(cx).as_ref()!=Some(&socket)||socket.read(cx).connection_generation()!=Some(generation) {return Err(refused("The workflow connection changed."));}
            Ok(socket.read(cx).call::<Value>("workflow","run",json!({"runId":run_id,"machine":machine,"after":after}),None))
        })?;
        let value=match call.await {Outcome::Ok(value)=>value,Outcome::Failed(error)=>return Err(refused(&error.message)),Outcome::Aborted=>return Err(refused("The workflow read was cancelled."))};
        if cx.update(|cx|SocketClient::try_bulk(cx).as_ref()!=Some(&socket)||socket.read(cx).connection_generation()!=Some(generation)) {return Err(refused("The workflow connection changed."));}
        let page=RunView::decode(value,run_id)?;
        if let Some(pages)=pages.as_mut(){pages.append(page)?;}else{pages=Some(RunPages::new(page)?);}
        if pages.as_ref().is_some_and(|pages|pages.view().next.is_none()){return Ok(pages.expect("installed page").finish());}
    }
}
fn refused(message:&str)->EngineRefusal {EngineRefusal{code:"workflow_read_unavailable".into(),message:message.into()}}

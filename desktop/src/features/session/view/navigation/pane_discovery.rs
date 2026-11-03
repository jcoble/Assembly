//! One optional current-route discovery phase inside the SAME pending-open supersession lifetime.
use gpui_kit::*;
use crate::core::{engine::SessionKey,intents::session_open::{SessionOpenRequest,SessionOpenTarget}};
use crate::features::session::client::source_resolution;
use super::{SessionPane,opening::{ConnectionCut,refused,call_refusal}};
impl SessionPane {
    pub fn request_current_route(&mut self,route:Option<String>,session:String,request_id:String,target:SessionOpenTarget,window:&mut Window,cx:&mut Context<Self>){
        self.cancel_preparation(refused("cancelled","The open was superseded by a newer request."),cx);
        if session.is_empty()||request_id.is_empty()||matches!(&target,SessionOpenTarget::Native{..}) {
            self.unknown_open_result(request_id,refused("bad_args","Current-route discovery requires an explicit Engine or historical session request."),cx);return;
        }
        let args=match source_resolution::build_args(route.as_deref()) {Ok(args)=>args,Err(error)=>{self.unknown_open_result(request_id,error,cx);return;}};
        let cut=match ConnectionCut::capture(cx){Ok(cut)=>cut,Err(error)=>{self.unknown_open_result(request_id,error,cx);return;}};
        let Some(serial)=self.open_serial.checked_add(1)else{self.unknown_open_result(request_id,refused("open_limit","Session open generation exhausted."),cx);return;};
        self.open_serial=serial;
        self.install_pending(None,request_id,cut,serial,window,cx);
        let call=self.client.call("sessions","resolve_source",args,None,cx);
        let read=cx.spawn(async move|pane,cx|{
            let answer=call.await.map_err(call_refusal);
            let _=pane.update(cx,|pane,cx|{
                if !pane.preparation_current(serial,cx){pane.refuse_replaced_preparation(serial,cx);return;}
                let pending=pane.pending_open.as_ref().expect("current discovery");
                let local=pending.cut.local_engine(cx);
                let source=answer.and_then(|value|source_resolution::parse_reply(value,route.as_deref(),local.as_deref()));
                let source=match source{Ok(source)=>source,Err(error)=>{pane.cancel_preparation(error,cx);return;}};
                let request=SessionOpenRequest{key:SessionKey{machine:source.machine.clone(),id:session},request_id:pending.request_id.clone(),target};
                let pending=pane.pending_open.as_mut().expect("same discovery generation");
                pending.classify_online=matches!(&request.target,SessionOpenTarget::Engine);
                pending.request=Some(request);pending.discovered_source=Some(source);
                // No new request serial: cancellation/replacement still owns the entire preparation.
                pane.start_target_read(serial,cx);
            });
        });
        self.pending_open.as_mut().expect("installed discovery").read=Some(read);
    }
}

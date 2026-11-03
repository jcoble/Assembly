//! Exact sessions/open_target wire adapter. Recorded checkout metadata grants no filesystem authority.
use serde::Deserialize;
use serde_json::{Value,json};
use crate::core::{engine::{EngineRefusal,SessionKey},intents::session_open::{SessionOpenRequest,SessionOpenTarget},socket::row_source::ExpectedRowSource};

pub(crate) struct OpenTarget {
    pub key:SessionKey,
    pub source:ExpectedRowSource,
    pub read_only:bool,
    pub has_history_rows:bool,
    pub recorded_checkout_root:Option<String>,
}
#[derive(Deserialize)]
#[serde(rename_all="camelCase",deny_unknown_fields)]
struct Read {
    kind:String,machine:String,generation:u64,route_machine:Option<String>,session_id:String,
    parent_session_id:Option<String>,item_id:Option<String>,seq:Option<i64>,payload_session_id:Option<String>,
    read_only:bool,has_history_rows:bool,original_key:Key,request_id:u64,checkout_root:Option<String>,
    expected_source:ExpectedRowSource,transcript_only:bool,
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Key {machine:String,id:String}

pub(crate) fn build_args(request:&SessionOpenRequest,serial:u64)->Result<Value,EngineRefusal> {
    request.validate_shape()?;
    let kind=kind(&request.target);
    let mut args=json!({"kind":kind,"machine":request.key.machine,"session":request.key.id,"requestId":serial});
    if let SessionOpenTarget::Native{parent,item_id,seq,payload_session_id}=&request.target {
        args["parent"]=json!({"machine":parent.machine,"id":parent.id});
        args["itemId"]=json!(item_id);args["seq"]=json!(seq);args["payloadSessionId"]=json!(payload_session_id);
    }
    Ok(args)
}

pub(crate) fn parse_response(value:Value,request:&SessionOpenRequest,serial:u64)->Result<OpenTarget,EngineRefusal> {
    request.validate_shape()?;
    let read:Read=serde_json::from_value(value).map_err(|error|refused("invalid_open_target_reply",&error.to_string()))?;
    let source=&read.expected_source;
    let tuple_valid=if source.generation==0 {source.route_machine.is_none()}
        else {source.route_machine.as_ref().is_some_and(|route|!route.is_empty())};
    if read.kind!=kind(&request.target) || read.machine!=request.key.machine || read.session_id.is_empty()
        || read.request_id!=serial || read.original_key.machine!=request.key.machine || read.original_key.id!=request.key.id
        || source.machine!=read.machine || source.generation!=read.generation || source.route_machine!=read.route_machine || !tuple_valid {
        return Err(refused("source_changed","The selected session source or request changed."));
    }
    if read.transcript_only!=read.checkout_root.as_ref().is_none_or(|root|root.trim().is_empty()) {
        return Err(refused("invalid_open_target_reply","The recorded checkout metadata is inconsistent."));
    }
    match &request.target {
        SessionOpenTarget::Native{parent,item_id,seq,payload_session_id}=>{
            if read.parent_session_id.as_deref()!=Some(parent.id.as_str()) || read.item_id.as_ref()!=Some(item_id)
                || read.seq!=Some(*seq) || read.payload_session_id.as_ref()!=Some(payload_session_id) || !read.read_only {
                return Err(refused("native_card_changed","The recorded native card changed."));
            }
            if !read.has_history_rows || read.session_id==parent.id {
                return Err(refused("native_history_unavailable","Readable native history is unavailable."));
            }
        }
        SessionOpenTarget::Engine|SessionOpenTarget::Historical=>{
            if read.session_id!=request.key.id || read.read_only!=matches!(request.target,SessionOpenTarget::Historical)
                || read.item_id.is_some() || read.seq.is_some() || read.payload_session_id.is_some() {
                return Err(refused("source_changed","The selected session identity or disposition changed."));
            }
        }
    }
    Ok(OpenTarget{key:SessionKey{machine:read.machine,id:read.session_id},source:read.expected_source,
        read_only:read.read_only,has_history_rows:read.has_history_rows,recorded_checkout_root:read.checkout_root})
}
fn kind(target:&SessionOpenTarget)->&'static str {
    match target {SessionOpenTarget::Engine=>"engine",SessionOpenTarget::Native{..}=>"native",SessionOpenTarget::Historical=>"historical"}
}
fn refused(code:&str,message:&str)->EngineRefusal {EngineRefusal{code:code.into(),message:message.into()}}

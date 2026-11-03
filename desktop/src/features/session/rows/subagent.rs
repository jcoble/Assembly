//! Persisted subagent display payload. For native cards SessionId is the recorded parent correlation;
//! the immutable owner row item supplies the provider-child Stop target. History identity is resolved by the owner.

use serde::Deserialize;

#[derive(Clone, Debug, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SubagentPayload {
    pub session_id: String,
    pub title: String,
    pub description: Option<String>,
    /// running, requires_action or idle.
    pub state: String,
    pub stop_reason: Option<String>,
    pub error: Option<String>,
    pub can_stop: bool,
    pub tool_call_id: Option<String>,
    #[serde(default)]
    pub helper: bool,
}

#[cfg(test)]
mod tests {
    use super::SubagentPayload;
    #[test]
    fn stop_uses_only_a_current_explicit_native_capability_and_the_persisted_child_suffix() {
        let mut card=SubagentPayload{session_id:"parent".into(),title:"Native".into(),description:None,state:"running".into(),stop_reason:None,error:None,can_stop:true,tool_call_id:None,helper:false};
        assert_eq!(card.stop_target("subagent:actual-provider-child",7),Some("actual-provider-child"));
        assert!(card.stop_target("parent",7).is_none());assert!(card.stop_target("subagent:",7).is_none());assert!(card.stop_target("subagent:child",0).is_none());
        card.helper=true;assert!(card.stop_target("subagent:child",7).is_none());
        card.helper=false;card.can_stop=false;assert!(card.stop_target("subagent:child",7).is_none());
        card.can_stop=true;card.state="idle".into();assert!(card.stop_target("subagent:child",7).is_none());
    }
}

impl SubagentPayload {
    /// A persisted provider-child Stop target, distinct from the payload's parent correlation or history identity.
    pub fn stop_target<'a>(&self,item_id:&'a str,seq:i64)->Option<&'a str> {
        if self.helper||!self.can_stop||self.state=="idle"||seq<=0{return None;}
        item_id.strip_prefix("subagent:").filter(|task|!task.is_empty())
    }
}
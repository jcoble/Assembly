//! Opens a session with words waiting in its composer, for the person to read, change and send there. Nothing is sent
//! for them: the session's own composer sends it, as it sends anything typed.
use crate::core::engine::SessionKey;
use gpui_kit::*;

#[derive(Clone, Debug, PartialEq, Action)]
#[action(namespace = intents, no_json)]
pub struct AskSession {
    pub key: SessionKey,
    pub text: String,
}

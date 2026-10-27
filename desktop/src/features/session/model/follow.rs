//! `Follow` decides where the timeline scrolls: a sent message scrolls to the top, "Jump to latest" pins the bottom
//! and keeps following while a turn writes, and the reader's own scroll ends both. It never runs past the prompt.
//! Ported from mac-command-bar's conversationScrollAnchor.ts (decideConversationScroll), whose tests come with it.

#[derive(Clone, Debug, Default, PartialEq)]
pub struct Follow {
    /// A send waits for its prompt's item, to scroll it to the top.
    pending_send: bool,
    /// "Jump to latest" pinned the bottom: growth keeps it there.
    pinned: bool,
}

/// What happened to the timeline.
#[derive(Clone, Debug, PartialEq)]
pub enum FollowEvent {
    Send,
    /// A user item arrived, with its key.
    UserItem(String),
    /// A reply grew.
    Growth,
    /// The reader scrolled, wheeled or pressed a key.
    ReaderInput,
    JumpToLatest,
}

/// What the timeline does about it.
#[derive(Clone, Debug, PartialEq)]
pub enum FollowStep {
    None,
    /// Scroll the item with this key to the top.
    ShowAtTop(String),
    ScrollToLatest,
}

impl Follow {
    pub fn on(&mut self, _event: FollowEvent) -> FollowStep {
        todo!()
    }
}

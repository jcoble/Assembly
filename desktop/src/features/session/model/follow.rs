//! `Follow` decides the one scroll the kit's list doesn't: a sent message's prompt goes to the top once its item
//! arrives (the old app's send anchor; whether it goes to the top or the end is open for Jesse). Following the end
//! while a turn writes, stopping when the reader scrolls, and "Jump to latest" are the kit's MessageScroller. Ported
//! from the send part of mac-command-bar's conversationScrollAnchor.ts.

#[derive(Clone, Debug, Default, PartialEq)]
pub struct Follow {
    /// A send waits for its prompt's item, to scroll it to the top.
    pending_send: bool,
}

/// What happened to the timeline.
#[derive(Clone, Debug, PartialEq)]
pub enum FollowEvent {
    Send,
    /// A user item arrived, with its key.
    UserItem(String),
    /// The reader scrolled before the prompt arrived: it stays where they put it.
    #[cfg_attr(
        not(test),
        expect(
            dead_code,
            reason = "the timeline doesn't tell the reader's scrolling from the list's own yet, so a sent prompt still goes to the top after the reader scrolls"
        )
    )]
    ReaderInput,
}

/// What the timeline does about it.
#[derive(Clone, Debug, PartialEq)]
pub enum FollowStep {
    None,
    /// Scroll the item with this key to the top.
    ShowAtTop(String),
}

impl Follow {
    pub fn on(&mut self, event: FollowEvent) -> FollowStep {
        match event {
            FollowEvent::Send => self.pending_send = true,
            FollowEvent::UserItem(key) if self.pending_send => {
                self.pending_send = false;
                return FollowStep::ShowAtTop(key);
            }
            FollowEvent::UserItem(_) => {}
            FollowEvent::ReaderInput => self.pending_send = false,
        }
        FollowStep::None
    }
}

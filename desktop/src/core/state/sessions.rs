//! `Sessions` owns the sessions list: the `sessions` topic's newest 200, newest first, each change upserted in place,
//! and older pages from `sessions/page` with the last one's (`before`, `beforeId`). Helper sessions aren't on it.

use gpui_kit::*;

use super::session_item::SessionItem;
use crate::core::socket::topic_subscription::TopicSubscription;
use crate::core::socket::update::Update;

pub struct Sessions {
    /// Newest first.
    items: Vec<SessionItem>,
    /// Where the next older page starts, (`nextBefore`, `nextBeforeId`); `None` once there are no more.
    older: Option<(i64, String)>,
    feed: Option<Task<()>>,
    subscription: Option<TopicSubscription<Update>>,
    loading_older: Option<Task<()>>,
}

impl Sessions {
    /// Subscribes to the `sessions` topic.
    pub fn new(_cx: &mut Context<Self>) -> Self {
        todo!()
    }

    pub fn items(&self) -> &[SessionItem] {
        &self.items
    }

    /// Loads the next older page, once at a time; nothing when there are no more.
    pub fn load_older(&mut self, _cx: &mut Context<Self>) {
        todo!()
    }

    fn apply(&mut self, _update: Update, _cx: &mut Context<Self>) {
        todo!()
    }
}

//! Tells the owner when a new question needs them, through the app's system notifications, and stays app-owned so a closed
//! Board still notifies. It listens to the shared rows and owns no timer: a mute only stops delivery, nothing replays when
//! it ends, and the row and badge never hide.
use std::collections::{HashMap, HashSet};

use gpui_kit::*;

use super::question_row::title;
use crate::core::engine::engine_items::{NeedItem, SessionItem};
use crate::core::engine::{EngineChanged, EngineFeed, EngineKey, SessionKey};
use crate::core::ui::activity::run_view::now_ms;

const PREVIEW_CHARS: usize = 120;

/// A notification's click, for whoever can show that question.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct OpenNeed(pub EngineKey);

pub struct OwnerNotify {
    feed: Entity<EngineFeed>,
    baselined: HashSet<String>,
    /// Every question already seen open, so a repeat event or a reconnect doesn't notify again.
    seen: HashSet<EngineKey>,
    /// What's on the owner's screen now, by the tag it was posted with.
    posted: HashMap<String, EngineKey>,
    muted_until: HashMap<String, i64>,
    _changed: Subscription,
}

struct GlobalNotify(Entity<OwnerNotify>);
impl Global for GlobalNotify {}
impl EventEmitter<OpenNeed> for OwnerNotify {}

/// The tag names the source machine and need, so a click always finds the same question.
pub fn tag(key: &EngineKey) -> String {
    format!("assembly:need:{}:{}", key.machine, key.id)
}

/// A preview's bound counts characters, so it never cuts one in half.
pub fn preview(text: &str) -> String {
    let flat = text.split_whitespace().collect::<Vec<_>>().join(" ");
    if flat.chars().count() <= PREVIEW_CHARS {
        return flat;
    }
    let mut cut = flat.chars().take(PREVIEW_CHARS - 1).collect::<String>();
    cut.push('…');
    cut
}

impl OwnerNotify {
    pub fn start(feed: Entity<EngineFeed>, cx: &mut App) -> Entity<Self> {
        let notify = cx.new(|cx| {
            let changed = cx.subscribe(&feed, |notify: &mut Self, _, _: &EngineChanged, cx| notify.follow(cx));
            Self { feed, baselined: HashSet::new(), seen: HashSet::new(), posted: HashMap::new(), muted_until: HashMap::new(), _changed: changed }
        });
        let weak = notify.downgrade();
        cx.on_system_notification_response(move |response, cx| {
            let _ = weak.update(cx, |notify, cx| notify.clicked(&response.tag, cx));
        });
        cx.set_global(GlobalNotify(notify.clone()));
        notify
    }

    pub fn try_global(cx: &App) -> Option<Entity<Self>> {
        cx.try_global::<GlobalNotify>().map(|global| global.0.clone())
    }

    /// The owner machine's mute, from its settings. Only delivery looks at this.
    pub fn set_muted_until(&mut self, machine: String, until: Option<i64>) {
        match until {
            Some(until) => self.muted_until.insert(machine, until),
            None => self.muted_until.remove(&machine),
        };
    }

    fn clicked(&mut self, tag: &str, cx: &mut Context<Self>) {
        let Some(key) = self.posted.get(tag).cloned() else { return };
        cx.activate(true);
        cx.emit(OpenNeed(key));
    }

    fn follow(&mut self, cx: &mut Context<Self>) {
        let feed = self.feed.read(cx);
        let machines = feed.loaded_machines("needs_me");
        let needs = feed.topic_items("needs_me").iter().filter_map(NeedItem::of).collect::<Vec<_>>();
        let asker = |session: &SessionKey| {
            let key = EngineKey { machine: session.machine.clone(), kind: "session".into(), id: session.id.clone() };
            feed.item(&key).and_then(SessionItem::of).and_then(|session| session.title).unwrap_or_else(|| "A session asks".to_owned())
        };
        let now = now_ms();
        let mut show = Vec::new();
        let mut open = HashSet::new();
        for need in needs.iter().filter(|need| need.state == "open" && need.addressee_session_id.is_none()) {
            open.insert(need.key.clone());
            if !self.seen.insert(need.key.clone()) || !self.baselined.contains(&need.key.machine) {
                continue;
            }
            if self.muted_until.get(&need.key.machine).is_some_and(|until| *until > now) {
                continue;
            }
            let body = need.text.as_deref().map_or_else(|| title(need), preview);
            show.push((need.key.clone(), asker(&need.session), body));
        }
        let loaded = machines.iter().cloned().collect::<HashSet<_>>();
        self.baselined.extend(machines);
        for (key, heading, body) in show {
            let posted = tag(&key);
            cx.show_system_notification(SystemNotification { tag: posted.clone().into(), title: heading.into(), body: body.into(), actions: Vec::new() });
            self.posted.insert(posted, key);
        }
        // A machine out of reach says nothing about its questions, so those stay until it's back.
        let settled = self.posted.iter().filter(|(_, key)| loaded.contains(&key.machine) && !open.contains(*key)).map(|(tag, _)| tag.clone()).collect::<Vec<_>>();
        for tag in settled {
            cx.dismiss_system_notification(&tag);
            self.posted.remove(&tag);
        }
    }
}

#[cfg(test)]
#[path = "owner_notify_tests.rs"]
mod tests;

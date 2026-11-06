//! Tells the owner when a new question needs them, through the app's system notifications, and stays app-owned so a closed
//! Board still notifies. It listens to the shared rows and owns no timer: a mute only stops delivery, nothing replays when
//! it ends, and the row and badge never hide.
use std::collections::{HashMap, HashSet};

use gpui_kit::*;

use super::mute_menu::{muted_until, settings_key};
use super::question_row::title;
use crate::core::engine::engine_items::{NeedItem, SessionItem};
use crate::core::engine::{EngineChanged, EngineFeed, EngineKey, SessionKey};
use crate::core::ui::activity::run_view::now_ms;

const PREVIEW_CHARS: usize = 120;

/// A question waiting to learn whether its machine is muted.
struct Post {
    key: EngineKey,
    heading: String,
    body: String,
}

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
    /// The mute is read fresh when a question is about to post, so a change from any window or phone counts.
    reading: HashMap<u64, Task<()>>,
    next_read: u64,
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
            let changed = cx.subscribe(&feed, |notify: &mut Self, _, _: &EngineChanged, cx| {
                notify.follow(cx)
            });
            Self {
                feed,
                baselined: HashSet::new(),
                seen: HashSet::new(),
                posted: HashMap::new(),
                reading: HashMap::new(),
                next_read: 0,
                _changed: changed,
            }
        });
        let weak = notify.downgrade();
        cx.on_system_notification_response(move |response, cx| {
            let _ = weak.update(cx, |notify, cx| notify.clicked(&response.tag, cx));
        });
        cx.set_global(GlobalNotify(notify.clone()));
        notify
    }

    pub fn try_global(cx: &App) -> Option<Entity<Self>> {
        cx.try_global::<GlobalNotify>()
            .map(|global| global.0.clone())
    }

    fn clicked(&mut self, tag: &str, cx: &mut Context<Self>) {
        let Some(key) = self.posted.get(tag).cloned() else {
            return;
        };
        cx.activate(true);
        cx.emit(OpenNeed(key));
    }

    fn follow(&mut self, cx: &mut Context<Self>) {
        let feed = self.feed.read(cx);
        let machines = feed.loaded_machines("needs_me");
        let needs = feed
            .topic_items("needs_me")
            .iter()
            .filter_map(NeedItem::of)
            .collect::<Vec<_>>();
        let asker = |session: &SessionKey| {
            let key = EngineKey {
                machine: session.machine.clone(),
                kind: "session".into(),
                id: session.id.clone(),
            };
            feed.item(&key)
                .and_then(SessionItem::of)
                .and_then(|session| session.title)
                .unwrap_or_else(|| "A session asks".to_owned())
        };
        let mut show: HashMap<String, Vec<Post>> = HashMap::new();
        let mut open = HashSet::new();
        for need in needs
            .iter()
            .filter(|need| need.state == "open" && need.for_owner)
        {
            open.insert(need.key.clone());
            if !self.seen.insert(need.key.clone()) || !self.baselined.contains(&need.key.machine) {
                continue;
            }
            let body = need.text.as_deref().map_or_else(|| title(need), preview);
            show.entry(need.key.machine.clone())
                .or_default()
                .push(Post {
                    key: need.key.clone(),
                    heading: asker(&need.session),
                    body,
                });
        }
        let loaded = machines.iter().cloned().collect::<HashSet<_>>();
        self.baselined.extend(machines);
        for (machine, posts) in show {
            self.post_unless_muted(machine, posts, cx);
        }
        // A machine out of reach says nothing about its questions, so those stay until it's back.
        let settled = self
            .posted
            .iter()
            .filter(|(_, key)| loaded.contains(&key.machine) && !open.contains(*key))
            .map(|(tag, _)| tag.clone())
            .collect::<Vec<_>>();
        for tag in settled {
            cx.dismiss_system_notification(&tag);
            self.posted.remove(&tag);
        }
    }

    /// Reads the machine's mute, then posts what's still open. A read that fails goes by the last mute known, since a
    /// question is worth a banner when in doubt.
    fn post_unless_muted(&mut self, machine: String, posts: Vec<Post>, cx: &mut Context<Self>) {
        let read = self
            .feed
            .update(cx, |feed, cx| feed.ensure_item(settings_key(&machine), cx));
        let id = self.next_read;
        self.next_read += 1;
        let reading = cx.spawn(async move |notify, cx| {
            let _ = read.await;
            let _ = notify.update(cx, |notify, cx| {
                notify.reading.remove(&id);
                let feed = notify.feed.read(cx);
                if muted_until(feed, &machine, now_ms()).is_some() {
                    return;
                }
                let posts = posts
                    .into_iter()
                    .filter(|post| {
                        feed.need(&post.key)
                            .is_some_and(|need| need.state == "open")
                    })
                    .collect::<Vec<_>>();
                for post in posts {
                    let posted = tag(&post.key);
                    cx.show_system_notification(SystemNotification {
                        tag: posted.clone().into(),
                        title: post.heading.into(),
                        body: post.body.into(),
                        actions: Vec::new(),
                    });
                    notify.posted.insert(posted, post.key);
                }
            });
        });
        self.reading.insert(id, reading);
    }
}

#[cfg(test)]
#[path = "owner_notify_tests.rs"]
mod tests;

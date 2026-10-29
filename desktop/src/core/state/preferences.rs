//! `Preferences` owns the window's view of the backend's `preferences` topic, as the `Appearance` Settings saves:
//! the topic's state and each change after it set a key, and a save sets it here at once, so the window follows a
//! slider as it moves, then sends `preferences/set`. It's there from launch, with the defaults, so a view can observe
//! it before the backend is; `start` subscribes once the socket client has started.

use std::collections::HashMap;

use gpui_kit::*;
use serde_json::{Value, json};

use super::appearance::{Appearance, EDITOR_FONT_SIZE, EDITOR_LINE_HEIGHT};
use crate::core::socket::client::SocketClient;
use crate::core::socket::outcome::Outcome;
use crate::core::socket::update::Update;

pub struct Preferences {
    appearance: Appearance,
    /// Owns the `preferences` subscription; dropping it unsubscribes.
    _feed: Option<Task<()>>,
    /// The last save of each key, while it's being written. A newer one replaces it, so the newest value wins.
    saves: HashMap<&'static str, Task<()>>,
    /// The value of each save still on its way, so an older save's echo doesn't undo it.
    pending: HashMap<String, Value>,
    /// The values the backend has: its topic's, and each save it took. A failed save puts its key's back.
    confirmed: HashMap<String, Value>,
    /// Why the last edit wasn't saved, until the next one.
    failure: Option<SharedString>,
}

struct GlobalPreferences(Entity<Preferences>);

impl Global for GlobalPreferences {}

impl Preferences {
    /// Makes the window's preferences, with the defaults, at launch.
    pub fn init(cx: &mut App) {
        let preferences = cx.new(|_| Preferences {
            appearance: Appearance::default(),
            _feed: None,
            saves: HashMap::new(),
            pending: HashMap::new(),
            confirmed: HashMap::new(),
            failure: None,
        });
        cx.set_global(GlobalPreferences(preferences));
    }

    /// Subscribes to the `preferences` topic, once the socket client has started.
    pub fn start(cx: &mut App) {
        let Some(preferences) = Self::try_global(cx) else {
            return;
        };
        let mut subscription = SocketClient::global(cx)
            .read(cx)
            .subscribe("preferences", None);
        preferences.update(cx, |preferences, cx| {
            preferences._feed = Some(cx.spawn(async move |this, cx| {
                while let Ok(update) = subscription.updates.recv().await {
                    if this
                        .update(cx, |preferences, cx| preferences.apply(update, cx))
                        .is_err()
                    {
                        return;
                    }
                }
            }));
        });
    }

    pub fn try_global(cx: &App) -> Option<Entity<Self>> {
        cx.try_global::<GlobalPreferences>()
            .map(|preferences| preferences.0.clone())
    }

    /// Reads what Settings has saved, or the defaults before the preferences are made (in a test).
    pub fn read<R>(cx: &App, read: impl FnOnce(&Appearance) -> R) -> R {
        match Self::try_global(cx) {
            Some(preferences) => read(&preferences.read(cx).appearance),
            None => read(&Appearance::default()),
        }
    }

    /// Why the last edit wasn't saved, while it's the last one.
    pub fn failure(cx: &App) -> Option<SharedString> {
        Self::try_global(cx).and_then(|preferences| preferences.read(cx).failure.clone())
    }

    /// The editor's face, text size and line height, which the file view and the diff draw in.
    pub fn editor_text(cx: &App) -> (SharedString, Pixels, Pixels) {
        Self::read(cx, |appearance| {
            (
                appearance.editor_font(),
                px(appearance.number(&EDITOR_FONT_SIZE)),
                px(appearance.number(&EDITOR_LINE_HEIGHT)),
            )
        })
    }

    /// Sets `key` to `value` here at once and saves it. With no socket the edit isn't made; a save the backend
    /// doesn't take puts back the value it has. Either way the page says why.
    pub fn save(key: &'static str, value: Value, cx: &mut App) {
        let Some(preferences) = Self::try_global(cx) else {
            return;
        };
        let Some(socket) = SocketClient::try_global(cx) else {
            preferences.update(cx, |preferences, cx| {
                preferences.failure = Some("Not saved: the backend isn't running.".into());
                cx.notify();
            });
            return;
        };
        let call = socket.read(cx).call::<Value>(
            "preferences",
            "set",
            json!({ "key": key, "value": value }),
            None,
        );
        preferences.update(cx, |preferences, cx| {
            preferences.appearance.set(key.into(), value.clone());
            preferences.pending.insert(key.into(), value.clone());
            preferences.failure = None;
            let save = cx.spawn(async move |this, cx| {
                let failure = match call.await {
                    Outcome::Ok(_) => None,
                    Outcome::Failed(failure) => Some(failure.message),
                    Outcome::Aborted => Some("the connection to the backend closed".into()),
                };
                _ = this.update(cx, |preferences, cx| {
                    preferences.saved(key, value, failure, cx)
                });
            });
            preferences.saves.insert(key, save);
            cx.notify();
        });
    }

    fn saved(&mut self, key: &str, value: Value, failure: Option<String>, cx: &mut Context<Self>) {
        self.pending.remove(key);
        match failure {
            None => {
                self.confirmed.insert(key.into(), value);
            }
            Some(reason) => {
                let confirmed = self.confirmed.get(key).cloned().unwrap_or_default();
                self.appearance.set(key.into(), confirmed);
                self.failure = Some(format!("Not saved: {reason}").into());
            }
        }
        cx.notify();
    }

    fn apply(&mut self, update: Update, cx: &mut Context<Self>) {
        let items = match update {
            Update::State(items) => items,
            Update::Change { key, item } => vec![(key, item)],
        };
        for (key, mut item) in items {
            let value = item.get_mut("value").map(Value::take).unwrap_or_default();
            self.confirmed.insert(key.clone(), value.clone());
            if !self.pending.contains_key(&key) {
                self.appearance.set(key, value);
            }
        }
        cx.notify();
    }
}

#[cfg(test)]
#[path = "preferences_tests.rs"]
mod tests;

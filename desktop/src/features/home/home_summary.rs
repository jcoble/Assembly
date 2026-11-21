//! `HomeSummary` holds the tiny always-on `settings_summary` topic (key `current`), so the Home button can show its
//! progress ring and amber state while the Home panel drop-down is closed and unsubscribed from the full
//! `settings_state` topic. Started once in `main.rs` alongside `Machines`, and observed by `HomeButton`.

use gpui_kit::*;

use super::settings_state::Summary;
use crate::core::socket::client::SocketClient;
use crate::core::socket::update::Update;

const TOPIC: &str = "settings_summary";
const KEY: &str = "current";

/// Holds the latest pushed `SettingsSummary` for the window's life, costing nothing while idle.
pub struct HomeSummary {
    /// The last pushed summary; `None` until the backend sends its first state frame.
    summary: Option<Summary>,
    /// Owns the subscription; dropping the global entity unsubscribes.
    _feed: Task<()>,
}

/// Set once the socket client has started; observing it lets `HomeButton` attach even if created earlier.
pub(crate) struct GlobalHomeSummary(Entity<HomeSummary>);

impl Global for GlobalHomeSummary {}

impl HomeSummary {
    /// Starts the window's summary subscription once the socket client is ready.
    pub fn start(cx: &mut App) {
        let state = cx.new(Self::new);
        cx.set_global(GlobalHomeSummary(state));
    }

    /// Returns the global summary handle once started.
    pub fn try_global(cx: &App) -> Option<Entity<Self>> {
        cx.try_global::<GlobalHomeSummary>()
            .map(|state| state.0.clone())
    }

    /// Subscribes to `settings_summary` (key `current`) for as long as the entity lives.
    pub fn new(cx: &mut Context<Self>) -> Self {
        let feed = match SocketClient::try_global(cx) {
            Some(client) => {
                let mut subscription = client.read(cx).subscribe(TOPIC, Some(KEY));
                cx.spawn(async move |this, cx| {
                    // Each pushed update replaces the held summary; the loop ends when this entity is gone.
                    while let Ok(update) = subscription.updates.recv().await {
                        if this.update(cx, |held, cx| held.apply(update, cx)).is_err() {
                            return;
                        }
                    }
                })
            }
            None => Task::ready(()),
        };
        Self {
            summary: None,
            _feed: feed,
        }
    }

    /// The latest backend-derived summary, if any has arrived.
    pub fn summary(&self) -> Option<&Summary> {
        self.summary.as_ref()
    }

    /// Applies an initial state or incremental change frame, notifying observers only when the summary changed.
    fn apply(&mut self, update: Update, cx: &mut Context<Self>) {
        let item = match update {
            Update::State(items) => items
                .into_iter()
                .find(|(key, _)| key == KEY)
                .map(|(_, item)| item),
            Update::Change { key, item } if key == KEY => Some(item),
            Update::Change { .. } => return,
        };
        let next = match item.map(serde_json::from_value::<Summary>) {
            Some(Ok(summary)) => Some(summary),
            Some(Err(error)) => {
                eprintln!("settings_summary: an item that doesn't read: {error}");
                return;
            }
            None => None,
        };
        if self.summary != next {
            self.summary = next;
            cx.notify();
        }
    }
}

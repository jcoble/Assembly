//! `HomePanel` is the drop-down a press on the Home button opens: the top line says what needs the user, then the
//! providers, each with one state and a grower with a line per machine, then the machines. It shows only what the
//! backend pushes on the `settings_state` topic and decides nothing itself. The top bar's `HomeButton` creates it on
//! open and drops it on close, and dropping it unsubscribes, so a closed panel costs nothing.
//! Its rows are drawn in `home_panel_rows.rs`.

use std::collections::HashMap;

use gpui_kit::component::{ActiveTheme as _, v_flex};
use gpui_kit::*;

use super::home_panel_advanced::MachineEdit;
use super::settings_state::SettingsState;
use super::state_words::summary_tone;
use crate::core::socket::client::SocketClient;
use crate::core::socket::update::Update;
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::core::ui::access::control_ids::home as ids;
use crate::core::ui::access::say;
use crate::core::ui::row_drop_down::RowDropDowns;

const TOPIC: &str = "settings_state";
/// The topic's one item.
const KEY: &str = "current";
const WIDTH: Pixels = px(360.);
/// Past this the panel scrolls inside, so it never runs off a short window.
const MAX_HEIGHT: Pixels = px(560.);

pub struct HomePanel {
    /// The last pushed answer; `None` until the first one comes.
    pub(super) state: Option<SettingsState>,
    /// Which provider's grower is open: one at a time.
    pub(super) drops: Entity<RowDropDowns>,
    /// Which machine line's Advanced or Details grower is open: one at a time, inside a provider's grower.
    pub(super) sub_drops: Entity<RowDropDowns>,
    /// Each machine line's Advanced editor, made when its grower first opens.
    pub(super) edits: HashMap<String, MachineEdit>,
    /// Calls the panel has sent and not yet heard back from; dropping the panel cancels them.
    pub(super) calls: Vec<Task<()>>,
    /// Owns the subscription; dropping the panel drops it, which unsubscribes.
    _feed: Task<()>,
}

impl HomePanel {
    /// Subscribes to `settings_state` for as long as the panel lives.
    pub fn new(cx: &mut Context<Self>) -> Self {
        let feed = match SocketClient::try_global(cx) {
            Some(client) => {
                let mut subscription = client.read(cx).subscribe(TOPIC, Some(KEY));
                cx.spawn(async move |this, cx| {
                    // Each pushed update replaces the panel's state; the loop ends when the panel is gone.
                    while let Ok(update) = subscription.updates.recv().await {
                        if this
                            .update(cx, |panel, cx| panel.apply(update, cx))
                            .is_err()
                        {
                            return;
                        }
                    }
                })
            }
            None => Task::ready(()),
        };
        Self {
            state: None,
            drops: cx.new(|_| RowDropDowns::default()),
            sub_drops: cx.new(|_| RowDropDowns::default()),
            edits: HashMap::new(),
            calls: Vec::new(),
            _feed: feed,
        }
    }

    /// The topic's state (at subscribe and after a reconnect) or a change carries the whole item; it replaces ours.
    fn apply(&mut self, update: Update, cx: &mut Context<Self>) {
        let item = match update {
            Update::State(items) => items
                .into_iter()
                .find(|(key, _)| key == KEY)
                .map(|(_, item)| item),
            Update::Change { key, item } if key == KEY => Some(item),
            Update::Change { .. } => return,
        };
        // An item this desktop can't read at all is logged and leaves the last good state in place.
        self.state = match item.map(serde_json::from_value::<SettingsState>) {
            Some(Ok(state)) => Some(state),
            Some(Err(error)) => {
                eprintln!("settings_state: an item that doesn't read: {error}");
                return;
            }
            None => None,
        };
        cx.notify();
    }

    /// The top line: the backend's sentence for what needs the user, or that everything is ready.
    fn summary(&self, cx: &App) -> AnyElement {
        let muted = cx.theme().muted_foreground;
        let Some(state) = &self.state else {
            return say(ids::SUMMARY, "Waiting for the backend…")
                .text_role(TextRole::Body, cx)
                .text_color(muted)
                .into_any_element();
        };
        let summary = &state.summary;
        let color = summary_tone(&summary.kind).color(cx);
        say(ids::SUMMARY, summary.message.clone())
            .text_role(TextRole::Title, cx)
            .text_color(color)
            .into_any_element()
    }
}

impl Render for HomePanel {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        // The top line, then the two lists; a list the backend hasn't sent yet draws nothing.
        let summary = self.summary(cx);
        let lists = self.state.clone().map(|state| {
            v_flex()
                .child(self.providers(&state, cx))
                .child(self.machines(&state, cx))
        });
        v_flex()
            .id(ids::PANEL)
            .debug_selector(|| "home-panel".into())
            .w(WIDTH)
            .max_h(MAX_HEIGHT)
            .overflow_y_scroll()
            .py_2()
            .child(div().px_3().pb_2().child(summary))
            .children(lists)
    }
}

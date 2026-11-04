//! `SessionRow` is one session in the rail, on the shared three-line row: its title (after its provider's mark, both
//! tinted while it runs) with how long it has run or its age, and an arrow when the row has something to open; icons
//! for its state and machine, then the remote machine's name and branch, all spelled out in a tooltip; and its newest
//! text, one line, or its project before it has any. The right-click menu moves it between Working and Done or
//! settles it (`sessions/mark`).

use std::rc::Rc;

use gpui_kit::assets::IconName;
use gpui_kit::component::menu::{ContextMenuExt as _, PopupMenuItem};
use gpui_kit::component::{ActiveTheme as _, Icon, Sizable as _};
use gpui_kit::*;

use super::history::age::{age, elapsed_text};
use super::presence::Presence;
use super::rail_page::RailEntry;
use super::status::Status;
use crate::core::ui::three_line_row::{RowArrow, ThreeLineRow};

/// Moves a session to a group: the pane's `mark`.
pub type OnMark = Rc<dyn Fn(String, Status, &mut App)>;
/// Opens the row's session.
pub type OnSession = Rc<dyn Fn(String, Option<String>, &mut Window, &mut App)>;

/// What a row needs from the pane besides its entry: its place, and what its clicks do.
#[derive(Clone)]
pub struct RowContext {
    pub selected: bool,
    pub needs_you: bool,
    /// Its machine didn't answer last time (`Machines::offline`).
    pub offline: bool,
    /// The backend clock's last tick, for its age or running time.
    pub now: Option<i64>,
    /// Present when the row has something to open: a question, a workflow or sessions it started.
    pub arrow: Option<RowArrow>,
    pub on_open: OnSession,
    pub on_mark: OnMark,
}

#[derive(IntoElement)]
pub struct SessionRow {
    entry: RailEntry,
    context: RowContext,
}

impl SessionRow {
    pub fn new(entry: RailEntry, context: RowContext) -> Self {
        SessionRow { entry, context }
    }
}

/// The words that start a row's second line: "offline" while its machine isn't answering, ahead of its state. Every
/// row names its state; one that isn't working, waiting, failed or marked Done is "Idle", an unread answer included
/// (the dot before its title says that).
pub fn status_words(presence: Presence, offline: bool) -> Vec<&'static str> {
    let mut words = Vec::new();
    if offline {
        words.push("offline");
    }
    words.push(presence.word().unwrap_or("Idle"));
    words
}

/// The rest of a row's second line: "Workbox Test · lane/rail". A local session is on This Mac.
pub fn details(entry: &RailEntry) -> String {
    let mut parts = vec![entry.machine.clone().unwrap_or_else(|| "This Mac".into())];
    parts.extend(entry.branch.clone().filter(|branch| !branch.is_empty()));
    parts.join(" · ")
}

/// The short form of `details`, beside the machine's icon: a remote machine's name and the branch. The icon says
/// a session is local, so that needs no words.
pub fn short_details(entry: &RailEntry) -> String {
    let mut parts: Vec<String> = entry.machine.iter().cloned().collect();
    parts.extend(entry.branch.clone().filter(|branch| !branch.is_empty()));
    parts.join(" · ")
}

/// The icon for a state, where it has one: working is the row's tint, and idle shows nothing.
pub fn state_icon(presence: Presence) -> Option<IconName> {
    match presence {
        Presence::NeedsYou => Some(IconName::BellDot),
        Presence::Failed => Some(IconName::CircleX),
        Presence::Done => Some(IconName::CircleCheck),
        Presence::Working | Presence::Unread | Presence::Idle => None,
    }
}

/// The colour of a state's word.
fn tint(presence: Presence, cx: &App) -> Hsla {
    let theme = cx.theme();
    match presence {
        Presence::NeedsYou => theme.warning,
        Presence::Working => theme.info,
        Presence::Failed => theme.danger,
        Presence::Done => theme.success,
        Presence::Unread | Presence::Idle => theme.muted_foreground,
    }
}

impl RenderOnce for SessionRow {
    fn render(self, _window: &mut Window, cx: &mut App) -> impl IntoElement {
        let theme = cx.theme();
        let (muted, accent, info) = (theme.muted_foreground, theme.caret, theme.info);
        let entry = self.entry;
        let title = match entry.title.as_deref().map(str::trim) {
            Some(title) if !title.is_empty() => title.to_string(),
            _ => entry.group.clone(),
        };
        let RowContext {
            selected,
            needs_you,
            offline,
            now,
            arrow,
            on_open,
            on_mark,
        } = self.context;
        let presence = Presence::of(&entry, needs_you);
        let running = entry.running;
        let id = entry.session_id.clone();
        let route = entry.machine.clone();
        // No spinner: its frames redraw the window for as long as a session runs, about 33% of a core in a debug build.
        let mark = Icon::empty()
            .path(entry.icon.clone())
            .small()
            .text_color(if running { info } else { muted })
            .into_any_element();
        let time = now.map(|now| match running {
            true => (elapsed_text(now, entry.updated_at).into(), info),
            false => (age(now, entry.updated_at).into(), muted),
        });
        let title_color = match (running, offline) {
            (true, _) => accent,
            (false, true) => muted,
            (false, false) => cx.theme().foreground,
        };
        let spelled_out: SharedString = {
            let mut parts: Vec<String> = status_words(presence, offline)
                .into_iter()
                .map(str::to_owned)
                .collect();
            parts.push(details(&entry));
            parts.join(" · ").into()
        };
        let state = state_icon(presence).map(|icon| {
            Icon::new(icon)
                .xsmall()
                .flex_none()
                .text_color(tint(presence, cx))
                .into_any_element()
        });
        let machine = Icon::new(match (offline, entry.machine.is_some()) {
            (true, _) => IconName::WifiOff,
            (false, true) => IconName::Server,
            (false, false) => IconName::Laptop,
        })
        .xsmall()
        .flex_none()
        .text_color(muted)
        .into_any_element();
        let opened = id.clone();
        let selector = format!("session-{id}");
        let status = Status::of(entry.completed_at);
        ThreeLineRow {
            key: format!("session:{id}").into(),
            lead: mark,
            title: title.into(),
            title_color,
            time,
            arrow,
            icons: state.into_iter().chain([machine]).collect(),
            second: short_details(&entry).into(),
            spelled_out,
            // Always a third line, so every row is the same height: its newest answer, or its project before it has one.
            third: entry
                .preview
                .clone()
                .unwrap_or_else(|| entry.group.clone())
                .into(),
            dot: presence == Presence::Unread,
        }
        .item(cx)
        .debug_selector(move || selector.clone())
        // The row's own surface says it's selected; the kit's item owns the hover and the selected fill.
        .selected(selected)
        .cursor_pointer()
        .on_click(move |_, window, cx| on_open(opened.clone(), route.clone(), window, cx))
        .context_menu(move |mut menu, _, _| {
            for (label, to) in status.moves() {
                let (id, on_mark) = (id.clone(), on_mark.clone());
                menu = menu.item(
                    PopupMenuItem::new(label).on_click(move |_, _, cx| on_mark(id.clone(), to, cx)),
                );
            }
            menu
        })
    }
}

#[cfg(test)]
#[path = "session_row_tests.rs"]
mod tests;

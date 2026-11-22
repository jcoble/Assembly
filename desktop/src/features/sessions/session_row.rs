//! `SessionRow` is one session in the rail, on the shared three-line row, as the look's session row: its state's
//! glyph (a shape as well as a colour), then its title with how long it has run or its age, and an arrow when the row
//! has something to open; its provider's glyph, then its machine's icon and name ("This Mac" for a local one), all
//! spelled out with the branch in a tooltip; and its newest text, one line, or its project before it has any. A row that needs you
//! is outlined in amber; the selected row gets the selected fill and line. The right-click menu moves it between Working and Done or
//! settles it (`sessions/mark`), and copies its link, saying what that link does while the pointer rests on it.

use std::rc::Rc;

use gpui_kit::assets::IconName;
use gpui_kit::component::Icon;
use gpui_kit::component::menu::{ContextMenuExt as _, PopupMenuItem};
use gpui_kit::*;

use super::history::age::{age, elapsed_text};
use super::presence::Presence;
use super::rail_page::RailEntry;
use super::status::Status;
use crate::core::navigation::external_urls::session_link;
use crate::core::theme::look::Look;
use crate::core::ui::three_line_row::{RowArrow, ThreeLineRow};
use crate::hinted_item::HintedItem;

/// The state glyph, the provider's glyph and the machine's icon (the mockup's `.st`, the sheet's 14 and `.l2 .i`).
const GLYPH: Pixels = px(16.);
const PROVIDER_GLYPH: Pixels = px(14.);
const MACHINE_GLYPH: Pixels = px(13.);

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

/// The machine a row's session is on, by name; a local session is on This Mac.
fn machine_name(entry: &RailEntry) -> String {
    entry.machine.clone().unwrap_or_else(|| "This Mac".into())
}

/// The rest of a row's spelled-out line: "Workbox Test · lane/rail".
pub fn details(entry: &RailEntry) -> String {
    let mut parts = vec![machine_name(entry)];
    parts.extend(entry.branch.clone().filter(|branch| !branch.is_empty()));
    parts.join(" · ")
}

/// The icon for a state that has its own: working and idle take the ring and the empty circle (`glyph`).
pub fn state_icon(presence: Presence) -> Option<IconName> {
    match presence {
        Presence::NeedsYou => Some(IconName::CircleQuestionMark),
        Presence::Failed => Some(IconName::TriangleAlert),
        Presence::Done => Some(IconName::CircleCheck),
        Presence::Working | Presence::Unread | Presence::Idle => None,
    }
}

/// The row's lead glyph and its colour: the state's icon, the still working ring while it runs (it never spins),
/// and the empty circle when nothing is going on.
fn glyph(presence: Presence, look: &Look) -> (IconName, Hsla) {
    let icon = state_icon(presence).unwrap_or(match presence {
        Presence::Working => IconName::LoaderCircle,
        _ => IconName::Circle,
    });
    let colour = match presence {
        Presence::NeedsYou => look.needs_you,
        Presence::Working => look.accent,
        Presence::Failed => look.problem,
        Presence::Done | Presence::Unread | Presence::Idle => look.idle,
    };
    (icon, colour)
}

impl RenderOnce for SessionRow {
    fn render(self, _window: &mut Window, cx: &mut App) -> impl IntoElement {
        let look = Look::of(cx);
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
        let (icon, colour) = glyph(presence, &look);
        let lead = Icon::new(icon)
            .size(GLYPH)
            .text_color(colour)
            .into_any_element();
        let time = now.map(|now| match running {
            true => (elapsed_text(now, entry.updated_at).into(), look.text_3),
            false => (age(now, entry.updated_at).into(), look.text_3),
        });
        let title_color = if offline { look.text_3 } else { look.text_1 };
        let spelled_out: SharedString = {
            let mut parts: Vec<String> = status_words(presence, offline)
                .into_iter()
                .map(str::to_owned)
                .collect();
            parts.push(details(&entry));
            parts.join(" · ").into()
        };
        let provider = Icon::empty()
            .path(entry.icon.clone())
            .size(PROVIDER_GLYPH)
            .flex_none()
            .text_color(look.text_2)
            .into_any_element();
        let machine = Icon::new(match (offline, entry.machine.is_some()) {
            (true, _) => IconName::WifiOff,
            (false, true) => IconName::Server,
            (false, false) => IconName::Laptop,
        })
        .size(MACHINE_GLYPH)
        .flex_none()
        .text_color(look.text_3)
        .into_any_element();
        // Every row has a 1px outline, clear unless it's selected or needs you, so none shifts when that changes.
        let outline = match (selected, needs_you) {
            (true, true) => look.needs_you_line,
            (true, false) => look.line_selected,
            (false, true) => look.needs_you_line_row,
            (false, false) => transparent_black(),
        };
        let opened = id.clone();
        let selector = format!("session-{id}");
        let status = Status::of(entry.completed_at);
        let link_machine = route.clone();
        ThreeLineRow {
            key: format!("session:{id}").into(),
            lead,
            title: title.into(),
            title_color,
            time,
            arrow,
            icons: vec![provider, machine],
            // The second line names the machine after its icon, "This Mac" for a local one, as the look's row does.
            second: machine_name(&entry).into(),
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
        .border_1()
        .border_color(outline)
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
            let (id, machine) = (id.clone(), link_machine.clone());
            menu.separator().item(
                HintedItem::new("copy-deeplink", "Copy deeplink")
                    .hint("An assembly:// link that opens this session from anywhere")
                    .on_click(move |_, cx| {
                        let link = session_link(&id, machine.as_deref());
                        cx.write_to_clipboard(ClipboardItem::new_string(link))
                    }),
            )
        })
    }
}

#[cfg(test)]
#[path = "session_row_menu_tests.rs"]
mod menu_tests;
#[cfg(test)]
#[path = "session_row_tests.rs"]
mod tests;

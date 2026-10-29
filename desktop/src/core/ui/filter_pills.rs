//! `FilterPills` is the one way a list offers quick filters, as the old app's filter-pills.svelte did: a row of pills
//! under a header, one per group. A group with picks shows its picked labels joined by "/" in option order, or "All"
//! when every option is picked. Clicking a group opens it: the row becomes "← Back" and the group's values, which
//! toggle; unpicking the last one clears the group. "← Back", Escape in the row, or a click outside it closes the group
//! and keeps the picks. While any group has picks, a round × at the row's start clears every group. The owner hears
//! each change (`FilterPillsChanged`), reads the picks from `state`, and saves them if it wants to. The old app's
//! ten-second idle close is dropped: the window keeps no timers.

use gpui_kit::assets::IconName;
use gpui_kit::component::{ActiveTheme as _, Icon, h_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

pub struct FilterOption {
    pub value: String,
    pub label: String,
}

pub struct FilterGroup {
    pub label: String,
    pub options: Vec<FilterOption>,
    /// The picked values, in option order.
    pub picked: Vec<String>,
}

impl FilterGroup {
    pub fn new(label: &str, options: Vec<FilterOption>) -> Self {
        FilterGroup {
            label: label.into(),
            options,
            picked: Vec::new(),
        }
    }
}

/// The groups with their picks, and which group is open.
pub struct Pills {
    pub groups: Vec<FilterGroup>,
    pub open: Option<usize>,
}

impl Pills {
    /// Picks `value` in group `group`, or drops it when it's picked; the picks stay in option order.
    pub fn toggle(&mut self, group: usize, value: &str) {
        let group = &mut self.groups[group];
        let was = |option: &&FilterOption| group.picked.contains(&option.value);
        group.picked = (group.options.iter())
            .filter(|option| was(option) != (option.value == value))
            .map(|option| option.value.clone())
            .collect();
    }

    /// Empties every group, and closes the open one.
    pub fn clear(&mut self) {
        self.groups
            .iter_mut()
            .for_each(|group| group.picked.clear());
        self.open = None;
    }

    pub fn open(&mut self, group: usize) {
        self.open = Some(group);
    }

    /// Closes the open group; the picks stay.
    pub fn close(&mut self) {
        self.open = None;
    }

    /// The group's pill: its picked labels, "All", or its name when nothing's picked.
    pub fn label(&self, group: usize) -> String {
        let group = &self.groups[group];
        let picked: Vec<&str> = (group.options.iter())
            .filter(|option| group.picked.contains(&option.value))
            .map(|option| option.label.as_str())
            .collect();
        match picked.len() {
            0 => group.label.clone(),
            all if all == group.options.len() => "All".into(),
            _ => picked.join("/"),
        }
    }
}

/// A pick changed: the owner reads them from `state`.
pub struct FilterPillsChanged;

impl EventEmitter<FilterPillsChanged> for FilterPills {}

pub struct FilterPills {
    pub state: Pills,
    /// The row's, so Escape closes the open group while focus is in it.
    focus: FocusHandle,
}

impl FilterPills {
    pub fn new(groups: Vec<FilterGroup>, cx: &mut Context<Self>) -> Self {
        FilterPills {
            state: Pills { groups, open: None },
            focus: cx.focus_handle(),
        }
    }

    /// Changes the picks with `pick`, redraws, and tells the owner.
    fn pick(&mut self, pick: impl FnOnce(&mut Pills), cx: &mut Context<Self>) {
        pick(&mut self.state);
        cx.emit(FilterPillsChanged);
        cx.notify();
    }

    fn close(&mut self, cx: &mut Context<Self>) {
        self.state.close();
        cx.notify();
    }
}

/// A 26 px round pill: the theme's light primary at medium weight when picked, its secondary fill with muted text
/// otherwise.
fn pill(id: impl Into<ElementId>, picked: bool, cx: &App) -> Stateful<Div> {
    let theme = cx.theme();
    let (fill, hover, text) = match picked {
        true => (theme.primary, theme.primary_hover, theme.primary_foreground),
        false => (
            theme.secondary,
            theme.secondary_hover,
            theme.muted_foreground,
        ),
    };
    h_flex()
        .id(id)
        .flex_none()
        .h(px(26.))
        .px(px(12.))
        .rounded(px(999.))
        .text_size(px(12.))
        .bg(fill)
        .text_color(text)
        .when(picked, |pill| pill.font_weight(FontWeight::MEDIUM))
        .cursor_pointer()
        .hover(move |pill| pill.bg(hover))
}

impl Render for FilterPills {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let row = h_flex()
            .id("filter-pills")
            .key_context("FilterPills")
            .track_focus(&self.focus)
            .flex_wrap()
            .gap(px(6.))
            .px(px(12.))
            .pb(px(10.))
            .on_key_down(cx.listener(|pills, event: &KeyDownEvent, _, cx| {
                if event.keystroke.key == "escape" && pills.state.open.is_some() {
                    pills.close(cx);
                }
            }))
            .on_mouse_down_out(cx.listener(|pills, _, _, cx| {
                if pills.state.open.is_some() {
                    pills.close(cx);
                }
            }));
        let Some(open) = self.state.open else {
            let picked = (self.state.groups.iter()).any(|group| !group.picked.is_empty());
            let clear = pill("filter-pills-clear", false, cx)
                .w(px(26.))
                .px_0()
                .justify_center()
                .child(Icon::new(IconName::X).size(px(14.)))
                .on_click(cx.listener(|pills, _, _, cx| pills.pick(Pills::clear, cx)));
            return row.when(picked, |row| row.child(clear)).children(
                (self.state.groups.iter().enumerate()).map(|(at, group)| {
                    pill(("filter-group", at), !group.picked.is_empty(), cx)
                        .child(self.state.label(at))
                        .on_click(cx.listener(move |pills, _, window, cx| {
                            window.focus(&pills.focus, cx);
                            pills.state.open(at);
                            cx.notify();
                        }))
                }),
            );
        };
        let group = &self.state.groups[open];
        let back = pill("filter-back", false, cx)
            .child("← Back")
            .on_click(cx.listener(|pills, _, _, cx| pills.close(cx)));
        row.child(back)
            .children(group.options.iter().enumerate().map(|(at, option)| {
                let value = option.value.clone();
                pill(
                    ("filter-value", at),
                    group.picked.contains(&option.value),
                    cx,
                )
                .child(option.label.clone())
                .on_click(cx.listener(move |pills, _, _, cx| {
                    pills.pick(|state| state.toggle(open, &value), cx)
                }))
            }))
    }
}

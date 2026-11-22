//! `FilterPills` is the one way a list offers quick filters, as the old app's filter-pills.svelte did: a row of pills
//! under a header, one per group. A group with picks shows its picked labels joined by "/" in option order, or "All"
//! when every option is picked. Clicking a group opens it: the row becomes "← Back" and the group's values, which
//! toggle; unpicking the last one clears the group. "← Back", Escape in the row, or a click outside it closes the group
//! and keeps the picks. While any group has picks, a round × at the row's start clears every group. The owner hears
//! each change (`FilterPillsChanged`), reads the picks from `state`, and saves them if it wants to. The old app's
//! ten-second idle close is dropped: the window keeps no timers.

use crate::components::chip_button::chip_button;
use crate::core::theme::metrics::{CHIP_HEIGHT, SPACE};
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::core::ui::access::Control as _;
use crate::core::ui::access::control_ids::{filter_back, filter_clear, filter_group, filter_value};
use gpui_kit::assets::IconName;
use gpui_kit::component::{Icon, h_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

/// A filter's chip: its chevron after the words, 5 from them, with 9 after it (the mockup's `.chip`).
const CHIP_GAP: Pixels = px(5.);
const CHIP_PADDING_RIGHT: Pixels = px(9.);
/// The chevron and the clear x.
const CHIP_ICON: Pixels = px(13.);

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
    /// The list these pills filter (`sessions`, `tasks`), which starts their ids in the accessibility tree.
    area: &'static str,
    /// The row's, so Escape closes the open group while focus is in it.
    focus: FocusHandle,
}

impl FilterPills {
    pub fn new(area: &'static str, groups: Vec<FilterGroup>, cx: &mut Context<Self>) -> Self {
        FilterPills {
            state: Pills { groups, open: None },
            area,
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

/// A chip's words and any chevron after them, 5 apart, in Meta, at medium weight when picked.
fn words(words: impl Into<SharedString>, picked: bool, cx: &App) -> Div {
    h_flex()
        .gap(CHIP_GAP)
        .text_role(TextRole::Meta, cx)
        .when(picked, |words| words.font_weight(FontWeight::MEDIUM))
        .child(words.into())
}

impl Render for FilterPills {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let row = h_flex()
            .id("filter-pills")
            .key_context("FilterPills")
            .track_focus(&self.focus)
            .flex_wrap()
            .gap(SPACE[2])
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
            let clear = chip_button("filter-pills-clear", false, cx)
                .control(filter_clear(self.area), "Clear filters")
                .w(CHIP_HEIGHT)
                .px_0()
                .child(Icon::new(IconName::X).size(CHIP_ICON))
                .on_click(cx.listener(|pills, _, _, cx| pills.pick(Pills::clear, cx)));
            return row.when(picked, |row| row.child(clear)).children(
                (self.state.groups.iter().enumerate()).map(|(at, group)| {
                    let label = self.state.label(at);
                    let picked = !group.picked.is_empty();
                    chip_button(("filter-group", at), picked, cx)
                        .control(
                            filter_group(self.area, &group.label),
                            format!("{} filter: {label}", group.label),
                        )
                        .pr(CHIP_PADDING_RIGHT)
                        .child(
                            words(label, picked, cx)
                                .child(Icon::new(IconName::ChevronDown).size(CHIP_ICON)),
                        )
                        .on_click(cx.listener(move |pills, _, window, cx| {
                            window.focus(&pills.focus, cx);
                            pills.state.open(at);
                            cx.notify();
                        }))
                }),
            );
        };
        let group = &self.state.groups[open];
        let back = chip_button("filter-back", false, cx)
            .control(filter_back(self.area), "Back to the filters")
            .child(words("← Back", false, cx))
            .on_click(cx.listener(|pills, _, _, cx| pills.close(cx)));
        row.child(back)
            .children(group.options.iter().enumerate().map(|(at, option)| {
                let value = option.value.clone();
                let picked = group.picked.contains(&option.value);
                chip_button(("filter-value", at), picked, cx)
                    .control(
                        filter_value(self.area, &group.label, &option.value),
                        option.label.clone(),
                    )
                    .toggled(picked)
                    .child(words(option.label.clone(), picked, cx))
                    .on_click(cx.listener(move |pills, _, _, cx| {
                        pills.pick(|state| state.toggle(open, &value), cx)
                    }))
            }))
    }
}

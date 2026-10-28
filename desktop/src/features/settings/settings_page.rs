//! `SettingsPage` owns the open Settings page: a sheet over the whole window, with the sections in a column on the
//! left under "Search settings", and the chosen section's cards of rows on the right. Each change is saved at once
//! through `Preferences`, which the window and the views follow. The page and everything it started (the search box,
//! the sliders, a machine call on its way) go when the sheet closes. Replaces SettingsDialog.svelte and
//! SettingsHost.svelte.

use std::cell::Cell;

use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::input::{Input, InputEvent, InputState};
use gpui_kit::component::slider::{SliderEvent, SliderState};
use gpui_kit::component::{ActiveTheme as _, Icon, Placement, Sizable as _, WindowExt as _};
use gpui_kit::component::{h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use serde_json::json;

use super::fonts;
use super::machine_edits::MachineEdits;
use super::search;
use super::section::Section;
use crate::core::state::appearance::{
    EDITOR_FONT_SIZE, EDITOR_LINE_HEIGHT, NumberSetting, TERMINAL_FONT_SIZE, TERMINAL_LINE_HEIGHT,
    UI_FONT_SIZE,
};
use crate::core::state::machines::{GlobalMachines, Machines};
use crate::core::state::preferences::Preferences;

/// The left column's width, the old app's.
const NAV_WIDTH: Pixels = px(208.);
/// The search box's width in the title row.
const SEARCH_WIDTH: Pixels = px(280.);

pub struct SettingsPage {
    pub(super) search: Entity<InputState>,
    /// The section picked in the left column; a search that leaves it out shows the first one left.
    pub(super) chosen: &'static str,
    pub(super) sliders: Vec<(NumberSetting, Entity<SliderState>)>,
    pub(super) ui_fonts: Vec<SharedString>,
    pub(super) code_fonts: Vec<SharedString>,
    /// The Connections form's name, SSH destination, port and token file.
    pub(super) machine_fields: [Entity<InputState>; 4],
    pub(super) machine_edits: MachineEdits,
    /// The button whose machine call is running, and the call; dropping it stops waiting. One call at a time.
    pub(super) machine_call: Option<(SharedString, Task<()>)>,
    _subscriptions: Vec<Subscription>,
}

impl SettingsPage {
    /// Opens the page over the window, unless a sheet is already up.
    pub fn open(window: &mut Window, cx: &mut App) {
        if window.has_active_sheet(cx) {
            return;
        }
        let page = cx.new(|cx| SettingsPage::new(window, cx));
        let search = page.read(cx).search.clone();
        // The sheet takes focus when it opens, so the search box takes it on the sheet's first draw.
        let first_draw = Cell::new(true);
        window.open_sheet_at(Placement::Left, cx, move |sheet, window, cx| {
            if first_draw.replace(false) {
                search.update(cx, |search, cx| search.focus(window, cx));
            }
            sheet
                .size(relative(1.))
                .resizable(false)
                .overlay(false)
                .px_0()
                .title(Self::title_row(&search, cx))
                .child(page.clone())
        });
    }

    fn new(window: &mut Window, cx: &mut Context<Self>) -> Self {
        let search = cx.new(|cx| {
            InputState::new(window, cx)
                .placeholder("Search settings")
                .clean_on_escape()
        });
        let mut subscriptions = vec![cx.subscribe(&search, |_, _, event, cx| {
            if let InputEvent::Change = event {
                cx.notify();
            }
        })];
        let numbers = [
            UI_FONT_SIZE,
            EDITOR_FONT_SIZE,
            EDITOR_LINE_HEIGHT,
            TERMINAL_FONT_SIZE,
            TERMINAL_LINE_HEIGHT,
        ];
        let sliders = numbers.map(|setting| {
            let value = Preferences::read(cx, |appearance| appearance.number(&setting));
            let slider = cx.new(|_| {
                SliderState::new()
                    .min(setting.min)
                    .max(setting.max)
                    .step(setting.step)
                    .default_value(value)
            });
            subscriptions.push(cx.subscribe(&slider, move |_, _, event, cx| {
                let SliderEvent::Change(value) = event else {
                    return;
                };
                let value = value.start();
                // A drag reports every move; only a new value is saved.
                if Preferences::read(cx, |appearance| appearance.number(&setting)) != value {
                    Preferences::save(setting.key, json!(value), cx);
                }
            }));
            (setting, slider)
        });
        if let Some(preferences) = Preferences::try_global(cx) {
            subscriptions.push(cx.observe_in(&preferences, window, |page, _, window, cx| {
                page.follow_preferences(window, cx)
            }));
        }
        subscriptions.push(cx.observe_global::<GlobalMachines>(|page, cx| {
            page.follow_machines(cx);
            cx.notify();
        }));
        let (ui_fonts, code_fonts) = fonts::installed(cx);
        let mut page = Self {
            search,
            chosen: "appearance",
            sliders: sliders.into(),
            ui_fonts,
            code_fonts,
            machine_fields: Self::machine_fields(window, cx),
            machine_edits: MachineEdits::default(),
            machine_call: None,
            _subscriptions: subscriptions,
        };
        page.follow_machines(cx);
        page
    }

    /// Redraws as the machines list changes.
    fn follow_machines(&mut self, cx: &mut Context<Self>) {
        if let Some(machines) = Machines::try_global(cx) {
            self._subscriptions
                .push(cx.observe(&machines, |_, _, cx| cx.notify()));
        }
    }

    /// Moves a slider whose value changed from elsewhere: the saved value arriving, or put back after a failed save.
    fn follow_preferences(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        for (setting, slider) in &self.sliders {
            let value = Preferences::read(cx, |appearance| appearance.number(setting));
            if slider.read(cx).value().start() != value {
                slider.update(cx, |slider, cx| slider.set_value(value, window, cx));
            }
        }
        cx.notify();
    }

    /// "Settings" and the search box, in the sheet's own title row beside its close button.
    fn title_row(search: &Entity<InputState>, cx: &App) -> impl IntoElement {
        h_flex()
            .flex_1()
            .justify_between()
            .pl(px(64.))
            .child(
                div()
                    .text_sm()
                    .font_weight(FontWeight::SEMIBOLD)
                    .text_color(cx.theme().foreground)
                    .child("Settings"),
            )
            .child(
                div().w(SEARCH_WIDTH).child(
                    Input::new(search)
                        .small()
                        .prefix(Icon::new(IconName::Search).small())
                        .cleanable(true),
                ),
            )
    }

    fn nav(
        &self,
        query: &str,
        shown: Option<&Section>,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let theme = cx.theme();
        let mut list = v_flex().flex_1().px_2().pt_2().gap_0p5();
        let mut group = "";
        for section in search::sections(query) {
            if section.group != group {
                group = section.group;
                list = list.child(
                    div()
                        .px_2()
                        .pt_3()
                        .pb_1()
                        .text_size(px(11.))
                        .font_weight(FontWeight::SEMIBOLD)
                        .text_color(theme.muted_foreground)
                        .child(group.to_uppercase()),
                );
            }
            let active = shown.is_some_and(|shown| shown.id == section.id);
            let id = section.id;
            list = list.child(
                h_flex()
                    .id(id)
                    .h(px(30.))
                    .px_2()
                    .gap_2()
                    .rounded(px(6.))
                    .text_sm()
                    .cursor_pointer()
                    .text_color(theme.sidebar_foreground)
                    .hover(|row| row.bg(theme.sidebar_accent.opacity(0.6)))
                    .when(active, |row| {
                        row.bg(theme.sidebar_accent)
                            .text_color(theme.sidebar_accent_foreground)
                    })
                    .child(Icon::new(section.icon).small())
                    .child(section.label)
                    .on_click(cx.listener(move |page, _, _, cx| {
                        page.chosen = id;
                        cx.notify();
                    })),
            );
        }
        v_flex()
            .w(NAV_WIDTH)
            .h_full()
            .flex_none()
            .bg(theme.sidebar)
            .border_r_1()
            .border_color(theme.sidebar_border)
            .child(list)
            .child(
                div()
                    .p_2()
                    .border_t_1()
                    .border_color(theme.sidebar_border)
                    .child(
                        Button::new("back-to-app")
                            .ghost()
                            .small()
                            .icon(IconName::ArrowLeft)
                            .label("Back to app")
                            .on_click(|_, window, cx| window.close_sheet(cx)),
                    ),
            )
    }
}

impl Render for SettingsPage {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let query = self.search.read(cx).value().to_string();
        let shown = search::shown(&query, self.chosen);
        h_flex()
            .size_full()
            .items_start()
            .child(self.nav(&query, shown, cx))
            .child(
                div()
                    .id("settings-content")
                    .flex_1()
                    .h_full()
                    .overflow_y_scroll()
                    .bg(cx.theme().background)
                    .child(self.content(&query, shown, cx)),
            )
    }
}

//! `SettingsPage` is the page in the Settings tab: GPUI Kit's `Settings`, with the sections in its sidebar over its search
//! box, and the chosen section's groups of settings on the right. Each change is saved at once through `Preferences`,
//! which the window and the views follow. The page and everything it started (the sliders, a machine call on its way)
//! go when the tab closes. Replaces SettingsDialog.svelte and SettingsHost.svelte.

use gpui_kit::component::group_box::GroupBoxVariant;
use gpui_kit::component::input::{InputEvent, InputState};
use gpui_kit::component::setting::{SelectIndex, Settings};
use gpui_kit::component::slider::{SliderEvent, SliderState};
use gpui_kit::*;
use serde_json::json;

use super::fonts;
use super::machine_edits::MachineEdits;
use super::section::SECTIONS;
use crate::core::state::appearance::{
    EDITOR_FONT_SIZE, EDITOR_LINE_HEIGHT, NumberSetting, TERMINAL_FONT_SIZE, TERMINAL_LINE_HEIGHT,
    UI_FONT_SIZE, UI_LINE_HEIGHT,
};
use crate::core::state::machines::{GlobalMachines, Machines};
use crate::core::state::preferences::Preferences;
use crate::core::state::sessions_settings::{self, CHILD_LIMIT, DEPTH_KEY};

/// The sections column's width, the old app's.
const NAV_WIDTH: Pixels = px(208.);

pub struct SettingsPage {
    pub(super) depth: Entity<InputState>,
    depth_dirty: bool,
    pub(super) depth_error: Option<&'static str>,
    pub(super) sliders: Vec<(NumberSetting, Entity<SliderState>)>,
    pub(super) ui_fonts: Vec<SharedString>,
    pub(super) code_fonts: Vec<SharedString>,
    /// The Connections form's name, SSH destination, port and token file.
    pub(super) machine_fields: [Entity<InputState>; 4],
    pub(super) ssh_picker: super::ssh_picker::SshPicker,
    pub(super) machine_edits: MachineEdits,
    /// The button whose machine call is running, and the call; dropping it stops waiting. One call at a time.
    pub(super) machine_call: Option<(SharedString, Task<()>)>,
    /// The page the kit's sidebar starts on, and a count that starts it there again when bumped.
    start: (usize, usize),
    /// Whether the last frame drew the machines, so the SSH hosts are read again each time they come into view.
    pub(super) machines_drawn: bool,
    focus: FocusHandle,
    _subscriptions: Vec<Subscription>,
}

impl SettingsPage {
    pub(super) fn new(window: &mut Window, cx: &mut Context<Self>) -> Self {
        let mut subscriptions = Vec::new();
        let depth = cx.new(|cx| InputState::new(window, cx));
        let depth_value = Preferences::read(cx, |appearance| {
            sessions_settings::depth(appearance.saved_value(DEPTH_KEY))
        });
        depth.update(cx, |input, cx| {
            input.set_value(depth_value.to_string(), window, cx)
        });
        subscriptions.push(cx.subscribe_in(
            &depth,
            window,
            |page, _, event, window, cx| match event {
                InputEvent::Change => {
                    page.depth_dirty = true;
                    page.depth_error = None;
                    cx.notify();
                }
                InputEvent::PressEnter { .. } | InputEvent::Blur => {
                    page.save_depth(window, cx);
                }
                _ => {}
            },
        ));
        let numbers = [
            UI_FONT_SIZE,
            UI_LINE_HEIGHT,
            EDITOR_FONT_SIZE,
            EDITOR_LINE_HEIGHT,
            TERMINAL_FONT_SIZE,
            TERMINAL_LINE_HEIGHT,
            CHILD_LIMIT,
        ];
        let sliders = numbers.map(|setting| {
            let value = Self::setting_value(setting, cx);
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
                if Self::setting_value(setting, cx) != value {
                    let value = if setting.key == CHILD_LIMIT.key {
                        json!(value as i32)
                    } else {
                        json!(value)
                    };
                    Preferences::save(setting.key, value, cx);
                }
            }));
            (setting, slider)
        });
        if let Some(preferences) = Preferences::try_global(cx) {
            subscriptions.push(cx.observe_in(&preferences, window, |page, _, window, cx| {
                page.follow_preferences(window, cx)
            }));
        }
        subscriptions.push(
            cx.observe_global_in::<GlobalMachines>(window, |page, window, cx| {
                page.follow_machines(window, cx);
                cx.notify();
            }),
        );
        let (ui_fonts, code_fonts) = fonts::installed(cx);
        let mut page = Self {
            depth,
            depth_dirty: false,
            depth_error: None,
            sliders: sliders.into(),
            ui_fonts,
            code_fonts,
            machine_fields: Self::machine_fields(window, cx),
            ssh_picker: super::ssh_picker::SshPicker::new(window, cx),
            machine_edits: MachineEdits::default(),
            machine_call: None,
            start: (0, 0),
            machines_drawn: false,
            focus: cx.focus_handle(),
            _subscriptions: subscriptions,
        };
        page.follow_machines(window, cx);
        page
    }

    /// Redraws as the machines list changes, and ends an edit whose machine went or changed elsewhere, emptying the
    /// form. Not while the form's own save is on its way: its change is the one coming back.
    fn follow_machines(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if let Some(machines) = Machines::try_global(cx) {
            let followed = cx.observe_in(&machines, window, |page, machines, window, cx| {
                let saving = (page.machine_call.as_ref()).is_some_and(|(id, _)| id == "set");
                if !saving && page.machine_edits.follow(machines.read(cx).machines()) {
                    page.clear_machine_fields(window, cx);
                }
                cx.notify();
            });
            self._subscriptions.push(followed);
        }
    }

    /// Moves a slider whose value changed from elsewhere: the saved value arriving, or put back after a failed save.
    fn follow_preferences(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if !self.depth_dirty {
            let value = Preferences::read(cx, |appearance| {
                sessions_settings::depth(appearance.saved_value(DEPTH_KEY)).to_string()
            });
            if self.depth.read(cx).value() != value {
                self.depth
                    .update(cx, |input, cx| input.set_value(value, window, cx));
            }
        }
        for (setting, slider) in &self.sliders {
            let value = Self::setting_value(*setting, cx);
            if slider.read(cx).value().start() != value {
                slider.update(cx, |slider, cx| slider.set_value(value, window, cx));
            }
        }
        cx.notify();
    }

    pub(super) fn setting_value(setting: NumberSetting, cx: &App) -> f32 {
        Preferences::read(cx, |appearance| {
            if setting.key == CHILD_LIMIT.key {
                sessions_settings::children(appearance.saved_value(setting.key)) as f32
            } else {
                appearance.number(&setting)
            }
        })
    }

    fn save_depth(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let draft = self.depth.read(cx).value().to_string();
        let value = match sessions_settings::parse_depth(&draft) {
            Ok(value) => value,
            Err(error) => {
                self.depth_error = Some(error);
                cx.notify();
                return;
            }
        };
        let saved = Preferences::read(cx, |appearance| {
            sessions_settings::depth(appearance.saved_value(DEPTH_KEY))
        });
        self.depth_dirty = false;
        self.depth_error = None;
        let text = value.to_string();
        self.depth
            .update(cx, |input, cx| input.set_value(text, window, cx));
        if value != saved {
            Preferences::save(DEPTH_KEY, json!(value), cx);
        }
        cx.notify();
    }
}

impl SettingsPage {
    /// Shows the section `id`, as a click on its entry in the sidebar would.
    pub(super) fn show(&mut self, id: &str, cx: &mut Context<Self>) {
        let page = SECTIONS
            .iter()
            .position(|section| section.id == id)
            .unwrap_or_default();
        self.start = (page, self.start.1 + 1);
        cx.notify();
    }
}

impl Render for SettingsPage {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        // The machines' item sets this again as it draws them; a frame without them forgets the hosts read.
        if !std::mem::take(&mut self.machines_drawn) {
            self.follow_ssh_hosts(false, window, cx);
        }
        let (page_ix, count) = self.start;
        div().size_full().track_focus(&self.focus).child(
            Settings::new(("settings", count))
                .sidebar_width(NAV_WIDTH)
                .with_group_variant(GroupBoxVariant::Outline)
                .default_selected_index(SelectIndex {
                    page_ix,
                    group_ix: None,
                })
                .pages(self.pages(cx)),
        )
    }
}

impl Focusable for SettingsPage {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus.clone()
    }
}

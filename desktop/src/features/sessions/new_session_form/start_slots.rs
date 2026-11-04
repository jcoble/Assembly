//! A new session's own controls inside the shared composer: which agent runs it and its settings in the box's footer,
//! and where it works directly under the box. They only present the form's pickers; what a pick does stays in the form.

use super::{NewSessionForm, Rc};
use crate::features::session::composer::composer_view::StartSlots;
use crate::features::session::composer::config_pills::{ConfigPills, OnSelect};
use crate::core::state::machines::Machines;
use gpui_kit::assets::IconName as Glyph;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::menu::{DropdownMenu as _, PopupMenuItem};
use gpui_kit::component::{ActiveTheme as _, Disableable as _, Icon, Sizable as _, h_flex};
use gpui_kit::*;

/// The composer draws these inside its own render, after the form's has finished, so each reads the form fresh.
pub(super) fn start_slots(form: WeakEntity<NewSessionForm>) -> StartSlots {
    let (for_name, for_agent, for_settings, for_place) =
        (form.clone(), form.clone(), form.clone(), form);
    StartSlots {
        provider: Rc::new(move |cx| {
            let form = for_name.upgrade()?;
            form.read(cx).agent_name().map(SharedString::from)
        }),
        footer: Rc::new(move |_, cx| {
            for_agent
                .upgrade()
                .map_or_else(|| Empty.into_any_element(), |form| agent_picker(form, cx))
        }),
        settings: Rc::new(move |compact, _, cx| {
            for_settings
                .upgrade()
                .map_or_else(|| Empty.into_any_element(), |form| settings(form, compact, cx))
        }),
        sub_bar: Rc::new(move |_, cx| {
            for_place
                .upgrade()
                .map_or_else(|| Empty.into_any_element(), |form| place_picker(form, cx))
        }),
    }
}

impl NewSessionForm {
    fn agent_name(&self) -> Option<&str> {
        let picked = self.agent.as_ref()?;
        let agent = self.agents.iter().find(|agent| &agent.id == picked)?;
        Some(&agent.name)
    }
}

fn agent_picker(form: Entity<NewSessionForm>, cx: &App) -> AnyElement {
    let read = form.read(cx);
    let name = read.agent_name().unwrap_or("Agent").to_string();
    let agents = read.agents.clone();
    let picked = read.agent.clone();
    let held = read.attempt.is_some();
    let sign_in = read.machine.is_none().then(|| read.sign_in_target());
    let can_sign_in = !read.starting && !held;
    let form = form.downgrade();
    Button::new("new:agent")
        .ghost()
        .xsmall()
        .label(name)
        .dropdown_caret(true)
        .disabled(held)
        .dropdown_menu_with_anchor(Anchor::BottomLeft, move |mut menu, _, _| {
            menu = menu.min_w(px(230.)).label("Which agent runs this");
            for agent in agents.clone() {
                let form = form.clone();
                let id = agent.id.clone();
                menu = menu.item(
                    PopupMenuItem::new(agent.name.clone())
                        .checked(picked.as_deref() == Some(&agent.id))
                        .on_click(move |_, _, cx| {
                            _ = form.update(cx, |form, cx| form.pick_agent(&id, cx));
                        }),
                );
            }
            if let Some(target) = sign_in.clone() {
                let form = form.clone();
                let usable = target.is_some() && can_sign_in;
                menu = menu.separator().item(
                    PopupMenuItem::new("Sign in")
                        .disabled(!usable)
                        .on_click(move |_, window, cx| {
                            if let Some(target) = target.clone() {
                                _ = form.update(cx, |form, cx| form.open_sign_in(target, window, cx));
                            }
                        }),
                );
            }
            menu
        })
        .into_any_element()
}

fn settings(form: Entity<NewSessionForm>, compact: bool, cx: &App) -> AnyElement {
    let read = form.read(cx);
    let Some(config) = &read.config else {
        return Empty.into_any_element();
    };
    let generation = read.generation;
    let options = (config.config.menus().into_iter())
        .map(|(option, current)| (option.clone(), current.to_string()))
        .collect();
    let provider = read.agent_name().unwrap_or("the agent").to_string();
    let disabled = read.starting || read.attempt.is_some();
    let form = form.downgrade();
    let on_select: OnSelect = Rc::new(move |id, value, _, cx| {
        _ = form.update(cx, |form, cx| {
            if form.generation == generation {
                form.choose(id, value, cx);
            }
        });
    });
    ConfigPills {
        provider: provider.into(),
        options,
        on_select,
        disabled,
        saving: false,
        compact,
    }
    .into_any_element()
}

/// A project already says where it lives, so only a remote one's machine shows; with no project the place is a choice.
fn place_picker(form: Entity<NewSessionForm>, cx: &App) -> AnyElement {
    let read = form.read(cx);
    let here = read.machine.clone();
    if read.selected_project.is_some() {
        let muted = cx.theme().muted_foreground;
        return match here {
            Some(machine) => h_flex()
                .gap(px(6.))
                .text_size(px(12.))
                .text_color(muted)
                .child(Icon::new(Glyph::Cloud).size(px(14.)))
                .child(machine)
                .into_any_element(),
            None => Empty.into_any_element(),
        };
    }
    let machines: Vec<String> = Machines::try_global(cx)
        .map(|machines| {
            (machines.read(cx).machines())
                .filter_map(|machine| machine.name.clone())
                .collect()
        })
        .unwrap_or_default();
    let held = read.attempt.is_some();
    let words = here.clone().unwrap_or_else(|| THIS_COMPUTER.to_string());
    let glyph = if here.is_some() { Glyph::Cloud } else { Glyph::Monitor };
    let form = form.downgrade();
    Button::new("new:place")
        .ghost()
        .xsmall()
        .icon(glyph)
        .label(words)
        .dropdown_caret(true)
        .disabled(held)
        .dropdown_menu_with_anchor(Anchor::BottomLeft, move |mut menu, _, _| {
            menu = menu.min_w(px(230.)).label("Work in");
            for machine in std::iter::once(None).chain(machines.iter().cloned().map(Some)) {
                let (glyph, words) = match &machine {
                    Some(name) => (Glyph::Cloud, name.clone()),
                    None => (Glyph::Monitor, THIS_COMPUTER.to_string()),
                };
                let checked = here == machine;
                let form = form.clone();
                menu = menu.item(
                    PopupMenuItem::new(words)
                        .icon(Icon::new(glyph))
                        .checked(checked)
                        .on_click(move |_, _, cx| {
                            _ = form.update(cx, |form, cx| form.pick_machine(machine.clone(), cx));
                        }),
                );
            }
            menu
        })
        .into_any_element()
}

const THIS_COMPUTER: &str = "This computer";

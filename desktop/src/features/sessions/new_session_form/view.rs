use crate::core::theme::text::{TextRole, TextRoleExt as _};
use super::{DraftConfig, Machines, NewSessionForm, Rc};
use crate::features::session::composer::config_menu::{
    ConfigMenu, ConfigSettings, OnSelect, inline,
};
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::input::Textarea;
use gpui_kit::component::{ActiveTheme as _, Disableable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

impl Render for NewSessionForm {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let theme = cx.theme();
        let muted = theme.muted_foreground;
        let machines: Vec<Option<String>> = Machines::try_global(cx)
            .map(|machines| {
                machines
                    .read(cx)
                    .machines()
                    .map(|machine| machine.name.clone())
                    .collect()
            })
            .unwrap_or_default();
        let mut places = h_flex().gap_1().flex_wrap();
        // This computer always, then each remote machine.
        for machine in std::iter::once(None).chain(machines.into_iter().filter(Option::is_some)) {
            let label = machine
                .clone()
                .unwrap_or_else(|| "This computer".to_string());
            let picked = self.machine == machine;
            let id = format!("new:machine:{label}");
            places = places.child(
                Button::new(SharedString::from(id))
                    .compact()
                    .when(picked, |button| button.primary())
                    .when(!picked, |button| button.ghost())
                    .label(label)
                    .on_click(
                        cx.listener(move |form, _, _, cx| form.pick_machine(machine.clone(), cx)),
                    ),
            );
        }
        let mut agents = h_flex().gap_1().flex_wrap();
        for agent in self.agents.clone() {
            let (id, name) = (agent.id, agent.name);
            let picked = self.agent.as_deref() == Some(&id);
            agents = agents.child(
                Button::new(SharedString::from(format!("new:agent:{id}")))
                    .compact()
                    .when(picked, |button| button.primary())
                    .when(!picked, |button| button.ghost())
                    .label(name)
                    .on_click(cx.listener(move |form, _, _, cx| {
                        form.pick_agent(&id, cx);
                    })),
            );
        }
        if self.machine.is_none() {
            let target = self.sign_in_target();
            agents = agents.child(
                Button::new("new:sign-in")
                    .compact()
                    .ghost()
                    .label("Sign in")
                    .disabled(target.is_none() || self.starting)
                    .on_click(cx.listener(move |form, _, window, cx| {
                        if let Some(target) = target.clone() {
                            form.open_sign_in(target, window, cx);
                        }
                    })),
            );
        }
        let generation = self.generation;
        let form = cx.weak_entity();
        let on_select: OnSelect = Rc::new(move |id, value, _, cx| {
            _ = form.update(cx, |form, cx| {
                if form.generation == generation {
                    form.choose(id, value, cx);
                }
            });
        });
        let mut controls = h_flex().gap_1p5().flex_wrap();
        if let Some(config) = &self.config {
            let (models, settings): (Vec<_>, Vec<_>) = config
                .config
                .menus()
                .into_iter()
                .partition(|(o, _)| inline(o));
            for (option, current) in models {
                controls = controls.child(ConfigMenu {
                    session: format!("new:{generation}"),
                    config_id: option.id.clone(),
                    label: option.name.clone().into(),
                    choices: option
                        .options
                        .iter()
                        .map(|c| (c.value.clone(), c.name.clone().into()))
                        .collect(),
                    current: current.into(),
                    on_select: on_select.clone(),
                    disabled: self.starting,
                });
            }
            if !settings.is_empty() {
                controls = controls.child(ConfigSettings {
                    options: settings
                        .into_iter()
                        .map(|(o, v)| (o.clone(), v.into()))
                        .collect(),
                    on_select,
                    disabled: self.starting,
                });
            }
        }
        v_flex()
            .gap_2()
            .p_3()
            .border_b_1()
            .border_color(theme.border)
            .child(div().text_role(TextRole::Meta, cx).text_color(muted).child("Machine"))
            .child(places)
            .child(div().text_role(TextRole::Meta, cx).text_color(muted).child("Agent"))
            .child(agents)
            .when(!self.auth_error.is_empty(), |view| {
                view.child(
                    div()
                        .text_role(TextRole::Body, cx)
                        .text_color(cx.theme().danger)
                        .child(self.auth_error.clone()),
                )
            })
            .child(Textarea::new(&self.folder).disabled(self.starting))
            .child(controls)
            .when(self.discovering, |form| {
                form.child(
                    div()
                        .text_role(TextRole::Meta, cx)
                        .text_color(muted)
                        .child("Loading agent settings…"),
                )
            })
            .when(!self.error.is_empty(), |form| {
                form.child(
                    div()
                        .text_role(TextRole::Meta, cx)
                        .text_color(theme.danger)
                        .child(self.error.clone()),
                )
            })
            .child(
                Button::new("new:create")
                    .primary()
                    .compact()
                    .label(if self.starting {
                        "Starting…"
                    } else {
                        "Start session"
                    })
                    .disabled(
                        self.starting
                            || (std::path::Path::new(self.folder.read(cx).value().trim())
                                .is_absolute()
                                && !self.config.as_ref().is_some_and(DraftConfig::ready)),
                    )
                    .on_click(cx.listener(|form, _, _, cx| form.create(cx))),
            )
    }
}

mod connections_view;
mod controls;
#[path = "../fonts.rs"]
mod fonts;
mod model;
mod operations;
mod provider_view;
#[path = "../row.rs"]
mod row;
// The production section model also carries navigation group metadata.
#[allow(dead_code)]
#[path = "../section.rs"]
mod section;

use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::inline_field::{
    FieldValue, FormFailure, FormSaveRequested, FormValues, InlineField, InlineForm,
};
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::group_box::GroupBoxVariant;
use gpui_kit::component::input::InputState;
use gpui_kit::component::label::Label;
use gpui_kit::component::setting::{SelectIndex, SettingGroup, SettingItem, SettingPage, Settings};
use gpui_kit::component::{ActiveTheme as _, Sizable as _, h_flex, v_flex};
use gpui_kit::*;
use model::Connections;
use std::collections::{HashMap, HashSet};

struct ProfileFields {
    form: Entity<InlineForm>,
    _saved: Subscription,
}

/// Presentation fixtures only: never opens a backend, socket, database or file.
pub struct SettingsPreview {
    connections: Connections,
    navigation_generation: usize,
    controls: Vec<controls::Control>,
    name: Entity<InputState>,
    destination: Entity<InputState>,
    inline_profiles: HashMap<u64, ProfileFields>,
    removing: Option<usize>,
    expanded: HashSet<u64>,
    provider_details: HashSet<(u64, usize)>,
    editing: HashSet<u64>,
    show_install: bool,
    notice: String,
    context_machine: Option<String>,
    /// So a click on the page's blank space leaves the inline field being edited.
    surface: FocusHandle,
}

impl SettingsPreview {
    pub fn new(window: &mut Window, cx: &mut Context<Self>) -> Self {
        let controls = controls::create(window, cx);
        let mut view = Self {
            connections: Connections::sample(),
            navigation_generation: 0,
            controls,
            name: cx.new(|cx| InputState::new(window, cx).placeholder("Machine name")),
            destination: cx
                .new(|cx| InputState::new(window, cx).placeholder("user@hostname or SSH alias")),
            inline_profiles: HashMap::new(),
            removing: None,
            expanded: HashSet::new(),
            provider_details: HashSet::new(),
            editing: HashSet::new(),
            show_install: false,
            notice: String::new(),
            context_machine: None,
            surface: cx.focus_handle(),
        };
        for index in 0..view.connections.machines.len() {
            view.add_inline_profile(index, window, cx);
        }
        view
    }

    pub fn focus_machine(&mut self, machine: Option<&str>, cx: &mut Context<Self>) {
        self.context_machine = machine.map(str::to_owned);
        self.show_install = machine.is_none();
        if let Some(machine) =
            machine.and_then(|name| self.connections.machines.iter().find(|m| m.name == name))
        {
            self.expanded.insert(machine.id);
        }
        self.navigation_generation += 1;
        cx.notify();
    }

    fn clear_form(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.name.update(cx, |s, cx| s.set_value("", window, cx));
        self.destination
            .update(cx, |s, cx| s.set_value("", window, cx));
    }

    fn save(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let name = self.name.read(cx).value().to_string();
        let destination = self.destination.read(cx).value().to_string();
        match self.connections.save(&name, &destination, None) {
            Ok(()) => {
                self.add_inline_profile(self.connections.machines.len() - 1, window, cx);
                self.clear_form(window, cx);
                self.notice = "Sample profile saved in memory. No machine was contacted.".into();
            }
            Err(message) => self.notice = message.into(),
        }
        cx.notify();
    }

    fn add_inline_profile(&mut self, index: usize, window: &mut Window, cx: &mut Context<Self>) {
        let machine = &self.connections.machines[index];
        let id = machine.id;
        let (name, destination) = (machine.name.clone(), machine.destination.clone());
        let name = cx.new(|cx| InlineField::text("Name", name, window, cx).in_form());
        let destination =
            cx.new(|cx| InlineField::text("SSH destination", destination, window, cx).in_form());
        let form = cx.new(|cx| {
            InlineForm::new(
                vec![
                    ("name".into(), name),
                    ("destination".into(), destination),
                ],
                cx,
            )
        });
        let saved = cx.subscribe_in(
            &form,
            window,
            move |view, form, save: &FormSaveRequested, window, cx| {
                let result = view.save_profile(id, &save.values);
                if result.is_ok() {
                    view.notice =
                        "Sample profile updated in memory. No machine was contacted.".into();
                }
                form.update(cx, |form, cx| {
                    form.complete_save(save.request, result, window, cx);
                });
                cx.notify();
            },
        );
        self.inline_profiles.insert(
            id,
            ProfileFields {
                form,
                _saved: saved,
            },
        );
    }

    fn save_profile(&mut self, id: u64, values: &FormValues) -> Result<FormValues, FormFailure> {
        let text = |key| match values.get(key) {
            Some(FieldValue::Text(value)) => value.clone(),
            _ => String::new(),
        };
        let index = self
            .connections
            .machines
            .iter()
            .position(|machine| machine.id == id)
            .ok_or_else(|| FormFailure::Refused("This sample profile was removed.".to_owned()))?;
        self.connections
            .save(&text("name"), &text("destination"), Some(index))
            .map_err(|message| FormFailure::Refused(message.to_owned()))?;
        let saved = &self.connections.machines[index];
        Ok(FormValues(vec![
            ("name".into(), FieldValue::Text(saved.name.clone())),
            ("destination".into(), FieldValue::Text(saved.destination.clone())),
        ]))
    }
}

impl Render for SettingsPreview {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let mut pages = Vec::new();
        for section in &section::SECTIONS {
            let mut page = SettingPage::new(section.label).icon(section.icon);
            if section.id == "connections" {
                let view = cx.entity();
                page = page.group(
                    SettingGroup::new()
                        .w_full()
                        .max_w(px(720.))
                        .variant(GroupBoxVariant::Normal)
                        .item(
                            SettingItem::render(move |_, _, cx| {
                                view.update(cx, |view, cx| view.connection_content(cx))
                            })
                            .keywords([
                                "Connections",
                                "Machines",
                                "SSH",
                                "remote",
                                "Workbox",
                                "add",
                                "edit",
                                "remove",
                            ]),
                        ),
                );
            } else {
                let mut groups: Vec<(&str, SettingGroup)> = Vec::new();
                for control in self
                    .controls
                    .iter()
                    .filter(|control| control.section == section.id)
                {
                    let index = match groups.iter().position(|(card, _)| *card == control.card) {
                        Some(index) => index,
                        None => {
                            groups.push((
                                control.card,
                                SettingGroup::new()
                                    .title(control.card)
                                    .w_full()
                                    .max_w(px(720.)),
                            ));
                            groups.len() - 1
                        }
                    };
                    let field = control.field.clone();
                    let description = control.description;
                    let item = SettingItem::render(move |_, _, cx| {
                        v_flex()
                            .w_full()
                            .max_w(px(640.))
                            .gap_1()
                            .child(field.clone())
                            .child(
                                Label::new(description)
                                    .text_role(TextRole::Body, cx)
                                    .text_color(cx.theme().muted_foreground),
                            )
                    })
                    .keywords([
                        control.title,
                        control.keywords,
                        description,
                        control.card,
                        section.label,
                    ]);
                    groups[index].1 = groups[index].1.clone().item(item);
                }
                for (_, group) in groups {
                    page = page.group(group);
                }
                if matches!(section.id, "updates" | "plugins") {
                    let view = cx.entity();
                    page = page.group(
                        SettingGroup::new()
                            .title("Machines and providers")
                            .max_w(px(720.))
                            .item(
                                SettingItem::render(move |_, _, _| {
                                    Button::new("manage-connections")
                                        .outline()
                                        .label("Open Connections")
                                        .on_click({
                                            let view = view.clone();
                                            move |_, _, cx| {
                                                view.update(cx, |s, cx| {
                                                    s.context_machine = None;
                                                    s.show_install = false;
                                                    s.navigation_generation += 1;
                                                    cx.notify();
                                                })
                                            }
                                        })
                                })
                                .keywords([
                                    "connections",
                                    "adapters",
                                    "CLI",
                                    "updates",
                                    "providers",
                                ]),
                            ),
                    );
                }
                for body in operations::bodies(section.id) {
                    page = page.group(body);
                }
            }
            pages.push(page);
        }
        v_flex().size_full().track_focus(&self.surface)
            .child(Label::new("UI preview · sample edits stay in memory; application preferences cannot be saved here.").text_role(TextRole::Body, cx).text_color(cx.theme().muted_foreground).p_3())
            .child(div().flex_1().min_h_0().child(
                Settings::new(("kit-settings", self.navigation_generation))
                    .sidebar_width(px(220.))
                    .with_group_variant(GroupBoxVariant::Outline)
                    .default_selected_index(SelectIndex { page_ix: 4, group_ix: None })
                    .pages(pages)
            ))
    }
}

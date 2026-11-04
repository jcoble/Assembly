//! One machine-centric surface for connection and provider maintenance.
use super::*;
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use gpui_kit::component::Disableable as _;
use gpui_kit::component::collapsible::Collapsible;
use gpui_kit::component::form::{Field, Form};
use gpui_kit::component::group_box::{GroupBox, GroupBoxVariants as _};
use gpui_kit::component::input::Input;
use gpui_kit::component::tag::Tag;

impl SettingsPreview {
    fn machine_row(&self, index: usize, cx: &mut Context<Self>) -> AnyElement {
        let machine = &self.connections.machines[index];
        let id = machine.id;
        let open = self.expanded.contains(&id);
        let status = if machine.connected {
            "Connected · sample"
        } else if machine.installed {
            "Offline · sample"
        } else {
            "Not installed · sample"
        };
        let badge = Tag::secondary();
        let mut details = v_flex().gap_4().pt_3();
        let mut actions = h_flex()
            .gap_2()
            .flex_wrap()
            .child(
                Button::new(("toggle-connection", index))
                    .outline()
                    .small()
                    .label(if machine.connected {
                        "Disconnect"
                    } else {
                        "Connect"
                    })
                    .disabled(!machine.installed)
                    .on_click(cx.listener(move |s, _, _, cx| {
                        s.connections.machines[index].connected =
                            !s.connections.machines[index].connected;
                        s.notice =
                            "Sample connection state changed in memory. No remote was contacted."
                                .into();
                        cx.notify();
                    })),
            )
            .child(
                Button::new(("edit-connection", index))
                    .outline()
                    .small()
                    .label("Edit connection")
                    .on_click(cx.listener(move |s, _, _, cx| {
                        if !s.editing.remove(&id) {
                            s.editing.insert(id);
                            s.expanded.insert(id);
                        }
                        cx.notify();
                    })),
            )
            .child(
                Button::new(("uninstall", index))
                    .ghost()
                    .small()
                    .label("Uninstall backend")
                    .disabled(true),
            )
            .child(
                Button::new(("remove", index))
                    .ghost()
                    .small()
                    .label("Remove connection")
                    .on_click(cx.listener(move |s, _, _, cx| {
                        s.removing = Some(index);
                        cx.notify();
                    })),
            );
        if self.removing == Some(index) {
            actions = actions
                .child(
                    Button::new(("confirm-remove", index))
                        .primary()
                        .small()
                        .label("Confirm removal")
                        .on_click(cx.listener(move |s, _, _, cx| {
                            let removed = s.connections.machines.remove(index);
                            s.inline_profiles.remove(&removed.id);
                            s.expanded.remove(&removed.id);
                            s.editing.remove(&removed.id);
                            s.removing = None;
                            s.notice = "Sample profile removed. Nothing was uninstalled.".into();
                            cx.notify();
                        })),
                )
                .child(
                    Button::new(("cancel-remove", index))
                        .outline()
                        .small()
                        .label("Cancel")
                        .on_click(cx.listener(|s, _, _, cx| {
                            s.removing = None;
                            cx.notify();
                        })),
                );
        }
        details = details.child(actions);
        if self.editing.contains(&id) {
            details = details.child(
                v_flex().gap_3().max_w(px(384.)).children(
                    self.inline_profiles
                        .get(&id)
                        .into_iter()
                        .flat_map(|p| p.fields.iter().cloned()),
                ),
            );
        }
        details = details
            .child(Label::new("Assembly backend").text_role(TextRole::Body, cx))
            .child(
                Label::new("Version and protocol checks appear here in the connected app.")
                    .text_role(TextRole::Body, cx)
                    .text_color(cx.theme().muted_foreground),
            )
            .child(
                Button::new(("check", index))
                    .outline()
                    .small()
                    .label("Check connection")
                    .disabled(true),
            );
        let connection = GroupBox::new().id(("connection", index)).outline().child(
            Collapsible::new()
                .open(open)
                .child(
                    v_flex()
                        .gap_1()
                        .child(
                            h_flex()
                                .justify_between()
                                .gap_3()
                                .child(
                                    Button::new(("machine-disclosure", index))
                                        .ghost()
                                        .label(format!(
                                            "{} {}",
                                            if open { "⌄" } else { "›" },
                                            machine.name
                                        ))
                                        .on_click(cx.listener(move |s, _, _, cx| {
                                            if !s.expanded.remove(&id) {
                                                s.expanded.insert(id);
                                            }
                                            cx.notify();
                                        })),
                                )
                                .child(badge.small().child(status)),
                        )
                        .child(
                            Label::new(machine.destination.clone())
                                .text_role(TextRole::Body, cx)
                                .text_color(cx.theme().muted_foreground),
                        )
                        .child(self.provider_rows(id, machine.connected, machine.installed, cx)),
                )
                .content(details),
        );
        v_flex()
            .gap_2()
            .child(
                h_flex().justify_end().child(
                    Button::new(("update-connection", index))
                        .primary()
                        .small()
                        .label("Update CLI's and Adapters")
                        .disabled(true),
                ),
            )
            .child(connection)
            .into_any_element()
    }

    pub(super) fn connection_content(&mut self, cx: &mut Context<Self>) -> AnyElement {
        let mut content = v_flex().gap_4().w_full().max_w(px(720.))
            .child(h_flex().justify_between().gap_3()
                .child(div().text_role(TextRole::Display, cx).child("Connections"))
                .child(Button::new("install-connect").primary().small().label("Install and Connect")
                    .on_click(cx.listener(|s, _, _, cx| { s.show_install = !s.show_install; cx.notify(); }))))
            .child(Label::new("Install, connect and manage your machines here. Expand a machine or provider for maintenance details.").text_role(TextRole::Body, cx).text_color(cx.theme().muted_foreground))
            .child(Label::new("Sample data · connection edits stay in memory. Provider versions and models below are layout examples, not checks of your machines.").text_role(TextRole::Body, cx).text_color(cx.theme().muted_foreground));
        if self.show_install {
            content = content.child(GroupBox::new().id("install-form").outline()
                .child(Label::new("Add a remote machine"))
                .child(Form::vertical().small().max_w(px(384.))
                    .child(Field::new().label("Machine name").child(Input::new(&self.name)))
                    .child(Field::new().label("SSH destination").child(Input::new(&self.destination))))
                .child(Label::new("The connected app will install the backend and connect over SSH. Here you can save a sample profile to try the layout.").text_role(TextRole::Body, cx).text_color(cx.theme().muted_foreground))
                .child(h_flex().gap_2()
                    .child(Button::new("save-machine").primary().small().label("Save sample profile")
                        .on_click(cx.listener(|s, _, window, cx| s.save(window, cx))))
                    .child(Button::new("cancel-install").outline().small().label("Cancel")
                        .on_click(cx.listener(|s, _, window, cx| { s.clear_form(window, cx); s.show_install = false; cx.notify(); })))));
        }
        for index in 0..self.connections.machines.len() {
            content = content.child(self.machine_row(index, cx));
        }
        if !self.notice.is_empty() {
            content = content.child(Label::new(self.notice.clone()).text_role(TextRole::Body, cx));
        }
        content.into_any_element()
    }
}

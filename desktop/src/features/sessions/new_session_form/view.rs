use super::NewSessionForm;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::{ActiveTheme as _, Disableable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

impl Render for NewSessionForm {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let theme = cx.theme();
        let muted = theme.muted_foreground;
        v_flex()
            .gap_2()
            .p_3()
            .border_b_1()
            .border_color(theme.border)
            .when(!self.auth_error.is_empty(), |view| {
                view.child(
                    div()
                        .text_sm()
                        .text_color(cx.theme().danger)
                        .child(self.auth_error.clone()),
                )
            })
            .child(div().track_focus(&self.chooser_focus).child(
                Button::new("new:project-choice").label(self.selected_project.as_ref().map_or_else(||"No project".to_string(),|project|format!("{} · {}",project.title,if project.machine=="local"{"This computer"}else{&project.machine})))
                    .disabled(self.attempt.is_some())
                    .on_click(cx.listener(|form,_,window,cx| {
                        form.chooser_open=true;
                        form.project_chooser.update(cx,|chooser,cx|chooser.focus_search(window,cx));cx.notify();
                    })),
            ))
            .when(self.chooser_open,|view|view.child(self.project_chooser.clone()))
            .when(self.discovering, |form| {
                form.child(
                    div()
                        .text_xs()
                        .text_color(muted)
                        .child("Loading agent settings…"),
                )
            })
            .when(!self.error.is_empty(), |form| {
                form.child(
                    div()
                        .text_xs()
                        .text_color(theme.danger)
                        .child(self.error.clone()),
                )
            })
            .when(self.attempt.is_some() && !self.starting, |view| view.child(
                Button::new("new:reconcile").ghost().label("Reconcile original start")
                    .on_click(cx.listener(|form, _, _, cx| form.reconcile_start(cx))),
            ))
            .child(self.start_composer.clone())
    }
}

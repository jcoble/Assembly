//! Regular Kit controls demonstrating one atomic, preview-memory-only group save.
use super::group_state::GroupState;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::form::{Field, Form};
use gpui_kit::component::group_box::{GroupBox, GroupBoxVariants as _};
use gpui_kit::component::input::{Input, InputEvent, InputState};
use gpui_kit::component::label::Label;
use gpui_kit::component::{ActiveTheme as _, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

pub(super) struct GroupedSampleForm {
    state: GroupState,
    inputs: [Entity<InputState>; 2],
    notice: &'static str,
    _heard: Vec<Subscription>,
}
impl GroupedSampleForm {
    pub fn new(window: &mut Window, cx: &mut Context<Self>) -> Self {
        let inputs = [
            cx.new(|cx| InputState::new(window, cx)),
            cx.new(|cx| InputState::new(window, cx)),
        ];
        let heard = inputs
            .iter()
            .enumerate()
            .map(|(index, input)| {
                cx.subscribe_in(input, window, move |form, input, event, _, cx| {
                    if matches!(event, InputEvent::Change) {
                        form.state.change(index, input.read(cx).value().to_string());
                        cx.notify();
                    }
                })
            })
            .collect();
        Self {
            state: GroupState::new(["Assembly".into(), "Workbox".into()]),
            inputs,
            notice: "Sample only · Both fields are required. One Save stores both in preview memory.",
            _heard: heard,
        }
    }
    fn edit(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.state.edit();
        for (input, value) in self.inputs.iter().zip(&self.state.draft) {
            input.update(cx, |input, cx| input.set_value(value.clone(), window, cx));
        }
        self.inputs[0].update(cx, |input, cx| input.focus(window, cx));
        cx.notify();
    }
}
impl Render for GroupedSampleForm {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let mut form = Form::vertical().small().w_full().max_w(px(288.));
        for (index, title) in ["Display name", "Destination"].iter().enumerate() {
            let value = if self.state.editing {
                Input::new(&self.inputs[index])
                    .small()
                    .w_full()
                    .into_any_element()
            } else {
                Label::new(self.state.committed[index].clone()).into_any_element()
            };
            form = form.child(
                Field::new().label(*title).child(
                    v_flex()
                        .gap_1()
                        .child(value)
                        .when_some(self.state.errors[index], |row, error| {
                            row.child(Label::new(error).text_color(cx.theme().danger))
                        }),
                ),
            );
        }
        let mut actions = h_flex().gap_2();
        if self.state.editing {
            actions = actions.child(Button::new("group-save").small().primary().label("Save")
                .on_click(cx.listener(|form, _, _, cx| {
                    if form.state.save() { form.notice = "Both values stored together in preview memory only. No real save was performed."; }
                    cx.notify();
                })))
                .child(Button::new("group-cancel").small().ghost().label("Cancel")
                    .on_click(cx.listener(|form, _, _, cx| { form.state.cancel(); cx.notify(); })));
        } else {
            actions = actions.child(
                Button::new("group-edit")
                    .small()
                    .label("Edit")
                    .on_click(cx.listener(|form, _, window, cx| form.edit(window, cx))),
            );
        }
        GroupBox::new()
            .id("grouped-sample-form")
            .outline()
            .title("Save fields together")
            .child(Label::new(self.notice).text_color(cx.theme().muted_foreground))
            .child(form)
            .child(actions)
    }
}

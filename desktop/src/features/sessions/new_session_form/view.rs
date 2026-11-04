use super::{DraftEvent, NewSessionForm};
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::{ActiveTheme as _, Disableable as _, Icon, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

impl NewSessionForm {
    /// A draft for the Chat pane: the heading and composer centred, with the note and the discard button over them.
    pub fn in_center(window: &mut Window, cx: &mut Context<Self>) -> Self {
        let mut form = Self::new(window, cx);
        form.centered = true;
        form
    }

    pub fn focus_start(&self, window: &mut Window, cx: &mut Context<Self>) {
        self.start_composer
            .update(cx, |composer, cx| composer.focus_input(window, cx));
    }

    fn open_chooser(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.chooser_open = true;
        self.project_chooser
            .update(cx, |chooser, cx| chooser.focus_search(window, cx));
        cx.notify();
    }

    /// Why the agent can't be used, that its settings are loading, the last error, and a start left to reconcile.
    fn notices(&self, cx: &mut Context<Self>) -> Vec<AnyElement> {
        let (muted, danger) = (cx.theme().muted_foreground, cx.theme().danger);
        let line = |color: Hsla, words: SharedString| {
            div()
                .text_role(TextRole::Meta, cx)
                .text_color(color)
                .child(words)
                .into_any_element()
        };
        let mut notices = Vec::new();
        if !self.auth_error.is_empty() {
            notices.push(
                div()
                    .text_role(TextRole::Body, cx)
                    .text_color(danger)
                    .child(self.auth_error.clone())
                    .into_any_element(),
            );
        }
        if self.discovering {
            notices.push(line(muted, "Loading agent settings…".into()));
        }
        if !self.error.is_empty() {
            notices.push(line(danger, self.error.clone()));
        }
        if self.attempt.is_some() && !self.starting {
            notices.push(
                Button::new("new:reconcile")
                    .ghost()
                    .label("Reconcile original start")
                    .on_click(cx.listener(|form, _, _, cx| form.reconcile_start(cx)))
                    .into_any_element(),
            );
        }
        notices
    }

    /// The project, in the heading's words: its name on a tinted chip with a chevron.
    fn project_trigger(&self, cx: &mut Context<Self>) -> impl IntoElement + use<> {
        let name = self
            .selected_project
            .as_ref()
            .map_or_else(|| "No project".to_string(), |project| project.title.clone());
        Button::new("new:project-choice")
            .ghost()
            .compact()
            .h_auto()
            .px(px(6.))
            .py(px(1.))
            .rounded(px(6.))
            .bg(cx.theme().foreground.opacity(0.09))
            .disabled(self.attempt.is_some())
            .child(
                h_flex()
                    .gap(px(4.))
                    .text_role(TextRole::Title, cx)
                    .font_weight(FontWeight::BOLD)
                    .child(name)
                    .child(Icon::new(IconName::ChevronDown).size(px(14.)).opacity(0.7)),
            )
            .on_click(cx.listener(|form, _, window, cx| form.open_chooser(window, cx)))
    }

    fn centered_view(&mut self, cx: &mut Context<Self>) -> AnyElement {
        let muted = cx.theme().muted_foreground;
        let heading = |words: &'static str, trigger, end: &'static str, cx: &App| {
            h_flex()
                .flex_wrap()
                .justify_center()
                .gap(px(4.))
                .text_role(TextRole::Title, cx)
                .font_weight(FontWeight::BOLD)
                .child(words)
                .child(trigger)
                .child(end)
        };
        let trigger = self.project_trigger(cx);
        let title = match self.selected_project {
            Some(_) => heading("What should we build in", trigger, "?", cx),
            None => heading("What should we build?", trigger, "", cx),
        };
        let notices = self.notices(cx);
        v_flex()
            .size_full()
            .child(
                h_flex()
                    .justify_between()
                    .gap(px(8.))
                    .pt(px(6.))
                    .pr(px(10.))
                    .pb(px(6.))
                    .pl(px(14.))
                    .text_role(TextRole::Meta, cx)
                    .text_color(muted)
                    .child("Your conversation starts when you send.")
                    .child(
                        Button::new("new:discard")
                            .ghost()
                            .small()
                            .icon(IconName::X)
                            .tooltip("Discard this draft")
                            .disabled(self.attempt.is_some())
                            .on_click(cx.listener(|_, _, _, cx| cx.emit(DraftEvent::Discard))),
                    ),
            )
            .child(
                v_flex()
                    .flex_1()
                    .min_h_0()
                    .items_center()
                    .justify_end()
                    .gap(px(6.))
                    .px(px(24.))
                    .pb(px(16.))
                    .child(div().track_focus(&self.chooser_focus).child(title))
                    .when(self.chooser_open, |column| {
                        column.child(
                            div()
                                .w_full()
                                .max_w(px(360.))
                                .child(self.project_chooser.clone()),
                        )
                    })
                    .children(notices),
            )
            .child(self.start_composer.clone())
            .child(div().flex_1())
            .into_any_element()
    }

    fn dialog_view(&mut self, cx: &mut Context<Self>) -> AnyElement {
        let chooser = Button::new("new:project-choice")
            .label(self.selected_project.as_ref().map_or_else(
                || "No project".to_string(),
                |project| {
                    let machine = if project.machine == "local" {
                        "This computer"
                    } else {
                        &project.machine
                    };
                    format!("{} · {machine}", project.title)
                },
            ))
            .disabled(self.attempt.is_some())
            .on_click(cx.listener(|form, _, window, cx| form.open_chooser(window, cx)));
        let notices = self.notices(cx);
        v_flex()
            .gap_2()
            .p_3()
            .border_b_1()
            .border_color(cx.theme().border)
            .child(div().track_focus(&self.chooser_focus).child(chooser))
            .when(self.chooser_open, |view| {
                view.child(self.project_chooser.clone())
            })
            .children(notices)
            .child(self.start_composer.clone())
            .into_any_element()
    }
}

impl Render for NewSessionForm {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        if self.centered {
            self.centered_view(cx)
        } else {
            self.dialog_view(cx)
        }
    }
}

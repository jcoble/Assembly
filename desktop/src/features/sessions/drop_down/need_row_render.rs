//! `NeedRow`'s render: the need's title, detail, answer controls and its working, failed or settled state.
use super::*;

fn dismiss(cx: &mut Context<NeedRow>) -> impl IntoElement {
    div().debug_selector(|| "need-dismiss".into()).child(
        Button::new("dismiss-need")
            .ghost()
            .label("Dismiss")
            .on_click(cx.listener(|row, _, _, cx| row.answer(NeedAnswer::Dismiss, cx))),
    )
}

impl Render for NeedRow {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let theme = cx.theme();
        let title = self
            .need
            .presentation
            .as_ref()
            .and_then(|presentation| presentation.title.clone())
            .or_else(|| self.need.text.clone())
            .unwrap_or_else(|| "Needs you".into());
        let title = self
            .need
            .display_number
            .map_or(title.clone(), |number| format!("{number}. {title}"));
        let mut column = v_flex()
            .gap_2()
            .py_1()
            .child(div().font_weight(FontWeight::MEDIUM).child(title));
        if let Some((label, key)) = self
            .viewed
            .as_ref()
            .and_then(|viewed| need_relation(&self.need, viewed))
        {
            let title = self
                .feed
                .read(cx)
                .session(&key)
                .and_then(|session| session.title)
                .unwrap_or_else(|| key.id.clone());
            column = column.child(
                div()
                    .text_color(theme.muted_foreground)
                    .child(format!("{label} {title}")),
            );
        }
        if let Some(recommendation) = self
            .need
            .presentation
            .as_ref()
            .and_then(|presentation| presentation.recommendation.clone())
        {
            column = column.child(
                div()
                    .text_color(theme.muted_foreground)
                    .child(recommendation),
            );
        }
        if let Some(notice) = self.need.answer_notice() {
            column = column.child(div().text_color(theme.muted_foreground).child(notice));
        }
        let label = match &self.state {
            NeedRowState::Sending => Some("Sending answer…".to_string()),
            NeedRowState::Answered { here, by } => Some(if *here {
                "Answered here".into()
            } else {
                format!("Answered {by}")
            }),
            NeedRowState::Closed { state } => Some(state.to_string()),
            NeedRowState::Refused { reason } | NeedRowState::Stale { reason } => {
                Some(reason.clone())
            }
            NeedRowState::Failed { message } => Some(message.clone()),
            _ => None,
        };
        if let Some(label) = label {
            column = column.child(div().text_color(theme.muted_foreground).child(label));
        }
        if !self.available {
            return column.child("The current owning source is unavailable.");
        }
        if self.need.state != "open" {
            if let Some(answer) = &self.need.answer {
                column = column.child(self.need.answer_text.clone().unwrap_or_else(|| {
                    AskForm::shown(
                        self.need
                            .presentation
                            .as_ref()
                            .and_then(|presentation| presentation.schema.as_ref()),
                        answer,
                    )
                    .to_string()
                }));
            }
            return column;
        }
        if matches!(self.state, NeedRowState::Sending) {
            return column;
        }
        if !self.reconciled && self._read.as_ref().is_some_and(|read| !read.is_ready()) {
            return column.child("Checking the current question…");
        }
        // Nothing here could answer it, so it says so at once rather than behind a button. A failed Dismiss keeps its
        // way back through Review again, below.
        if let Some(notice) = self.need.presentation_notice()
            && matches!(self.state, NeedRowState::Idle | NeedRowState::Open)
        {
            return column
                .child(
                    div()
                        .debug_selector(|| "need-no-controls".into())
                        .text_color(theme.muted_foreground)
                        .child(notice),
                )
                .child(dismiss(cx));
        }
        if !matches!(self.state, NeedRowState::Open) {
            let label = if matches!(self.state, NeedRowState::Idle) {
                if self.need.kind == "question" {
                    "Answer"
                } else {
                    "Review"
                }
            } else {
                "Review again"
            };
            return column.child(
                div().debug_selector(|| "need-review".into()).child(
                    Button::new("review-need")
                        .ghost()
                        .label(label)
                        .on_click(cx.listener(|row, _, window, cx| {
                            if row.reconciled {
                                row.open(window, cx);
                            } else {
                                row.reconcile(row.serial, cx);
                            }
                        })),
                ),
            );
        }
        let on_action: controls::OnControl = {
            let row = cx.weak_entity();
            Rc::new(move |action, _, cx| {
                let _ = row.update(cx, |row, cx| row.control(action, cx));
            })
        };
        let mut answers = v_flex()
            .gap_2()
            .debug_selector(|| "need-answer-controls".into());
        if let Some(presentation) = &self.need.presentation {
            if let Some(payload) = presentation.provider_payload(
                &self.need.state,
                self.need.text.clone(),
                self.need.answer.clone(),
            ) {
                if presentation.input_mode == "form" && self.form.is_none() {
                    answers = answers.child("This form's input controls are unavailable.");
                } else {
                    let choices = presentation
                        .options
                        .iter()
                        .enumerate()
                        .map(|(index, option)| {
                            (option.id.clone(), option.label.clone(), index == 0)
                        })
                        .collect::<Vec<_>>();
                    answers = answers.child(controls::render(
                        &format!("need:{}:{}", self.key.machine, self.key.id),
                        &payload,
                        self.form.as_ref(),
                        &choices,
                        on_action,
                        cx,
                    ));
                }
            } else if presentation.input_mode == "text" {
                answers = answers
                    .child(
                        div()
                            .capture_key_down(cx.listener(|row, event: &KeyDownEvent, _, cx| {
                                if event.keystroke.key == "enter"
                                    && !event.keystroke.modifiers.shift
                                    && !event.is_held
                                {
                                    row.answer(
                                        NeedAnswer::Answer(json!(
                                            row.text.read(cx).value().to_string()
                                        )),
                                        cx,
                                    );
                                    cx.stop_propagation();
                                } else if event.keystroke.key == "escape" {
                                    row.state = NeedRowState::Idle;
                                    cx.notify();
                                    cx.stop_propagation();
                                }
                            }))
                            .child(Textarea::new(&self.text)),
                    )
                    .child(Button::new("answer-text").primary().label("Send").on_click(
                        cx.listener(|row, _, _, cx| {
                            row.answer(
                                NeedAnswer::Answer(json!(row.text.read(cx).value().to_string())),
                                cx,
                            )
                        }),
                    ));
            } else if presentation.input_mode == "choice" {
                let mut choices = h_flex().gap_2().flex_wrap();
                for (index, option) in presentation.options.iter().enumerate() {
                    let id = option.id.clone();
                    let button = Button::new(SharedString::from(format!("need-choice:{id}")))
                        .label(option.label.clone())
                        .on_click(cx.listener(move |row, _, _, cx| {
                            row.answer(NeedAnswer::Answer(json!(id)), cx)
                        }));
                    choices = choices.child(if index == 0 {
                        button.primary()
                    } else {
                        button.secondary()
                    });
                }
                answers = answers.child(choices);
            }
        }
        column.child(answers).child(dismiss(cx))
    }
}

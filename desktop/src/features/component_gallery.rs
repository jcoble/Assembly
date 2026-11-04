//! Every shared piece in each of its states, on sample data held in memory. Nothing here is saved or sent.
use std::rc::Rc;

use chrono::{NaiveDate, NaiveTime};
use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::description_list::DescriptionList;
use gpui_kit::component::input::{Input, InputState};
use gpui_kit::component::{ActiveTheme as _, Sizable as _, h_flex, v_flex};
use gpui_kit::*;

use crate::activity::{
    ActivitySection, CardBody, LinkChip, NeedsCard, NeedsState, RunRow, SessionRowValues, StatusChip, StepMark,
    StepRight, StepView, Tone, format_elapsed, session_row, step_list,
};
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::core::ui::row_drop_down::{GrowingBody, RowDropDowns};
use crate::core::ui::three_line_row::row_height;
use crate::inline_field::{
    ChoiceOption, FieldRule, FieldValue, FormRule, FormSaveRequested, InlineField, InlineForm, NumberFormat,
    SaveFailure, SaveRequested,
};

const NEED_KEYS: [&str; 2] = ["gallery-question", "gallery-fails"];

/// What a sample field's pretend owner does with a save.
#[derive(Clone, Copy)]
enum Owner {
    Saves,
    Refuses,
    NeverAnswers,
}

fn statuses() -> Vec<ChoiceOption> {
    vec![
        ChoiceOption::new("todo", "To do"),
        ChoiceOption::new("doing", "In progress").tone(Tone::Info),
        ChoiceOption::new("blocked", "Blocked").tone(Tone::Warning),
        ChoiceOption::new("done", "Done").tone(Tone::Success),
    ]
}

fn required() -> FieldRule {
    Rc::new(|value| match value {
        FieldValue::Text(text) if text.trim().is_empty() => Err("This can't be empty.".to_owned()),
        _ => Ok(()),
    })
}

pub struct ComponentGallery {
    surface: FocusHandle,
    fields: Vec<Entity<InlineField>>,
    form: Entity<InlineForm>,
    answer: Entity<InputState>,
    failed: Option<SharedString>,
    answered: bool,
    needs: Entity<RowDropDowns>,
    runs: Entity<RowDropDowns>,
    steps: Entity<RowDropDowns>,
    sessions: Entity<RowDropDowns>,
    _heard: Vec<Subscription>,
}

impl ComponentGallery {
    pub fn new(window: &mut Window, cx: &mut Context<Self>) -> Self {
        let mut heard = Vec::new();
        let [needs, runs, steps, sessions] = [(); 4].map(|_| {
            let drops = cx.new(|_| RowDropDowns::default());
            heard.push(cx.observe(&drops, |_, _, cx| cx.notify()));
            drops
        });
        let fields = Self::single_fields(&mut heard, window, cx);
        let form = Self::whole_form(&mut heard, window, cx);
        Self {
            surface: cx.focus_handle(),
            fields,
            form,
            answer: cx.new(|cx| InputState::new(window, cx).placeholder("Type your answer")),
            failed: None,
            answered: false,
            needs,
            runs,
            steps,
            sessions,
            _heard: heard,
        }
    }

    fn single_fields(
        heard: &mut Vec<Subscription>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Vec<Entity<InlineField>> {
        let date = NaiveDate::from_ymd_opt(2026, 11, 12);
        let time = NaiveTime::from_hms_opt(9, 30, 0).expect("a valid sample time");
        let date_time = date.and_then(|date| date.and_hms_opt(9, 30, 0));
        let samples: Vec<(Owner, Entity<InlineField>)> = vec![
            (Owner::Saves, cx.new(|cx| InlineField::text("Text", "Assembly", window, cx).rule(required()))),
            (Owner::Saves, cx.new(|cx| InlineField::text("Empty text", "", window, cx))),
            (Owner::Saves, cx.new(|cx| InlineField::multiline("Several lines", "First line\nSecond line", window, cx))),
            (Owner::Saves, cx.new(|cx| InlineField::email("Email", "name@example.com", window, cx))),
            (Owner::Saves, cx.new(|cx| InlineField::phone("Phone", "5551234567", window, cx))),
            (Owner::Saves, cx.new(|cx| InlineField::number("Currency", "1250.5", NumberFormat::Currency, window, cx))),
            (Owner::Saves, cx.new(|cx| InlineField::number("Percent", "12.5", NumberFormat::Percent, window, cx))),
            (Owner::Saves, cx.new(|cx| InlineField::number("Whole number", "1200", NumberFormat::Integer, window, cx))),
            (Owner::Saves, cx.new(|cx| InlineField::number("Decimal", "3.25", NumberFormat::Decimal, window, cx))),
            (Owner::Saves, cx.new(|cx| InlineField::number("ZIP", "90210", NumberFormat::Zip, window, cx))),
            (Owner::Saves, cx.new(|cx| InlineField::date("Date", date, window, cx))),
            (Owner::Saves, cx.new(|cx| InlineField::date_time("Date and time", date_time, window, cx))),
            (Owner::Saves, cx.new(|cx| InlineField::time("Time", time, window, cx))),
            (Owner::Saves, cx.new(|cx| InlineField::combo("Choice", statuses(), Some("doing".into()), window, cx))),
            (Owner::Saves, cx.new(|cx| InlineField::chip("Status chip", statuses(), Some("blocked".into()), window, cx))),
            (Owner::Saves, cx.new(|cx| InlineField::chip("A status that was renamed", statuses(), Some("Waiting on legal".into()), window, cx))),
            (Owner::Saves, cx.new(|cx| InlineField::text("Read only", "Can't be changed here", window, cx).read_only())),
            (Owner::Refuses, cx.new(|cx| InlineField::text("Its save is refused", "Change me", window, cx))),
            (Owner::NeverAnswers, cx.new(|cx| InlineField::chip("Stays saving", statuses(), Some("todo".into()), window, cx))),
        ];
        heard.extend(samples.iter().map(|(owner, field)| {
            let owner = *owner;
            cx.subscribe_in(field, window, move |_, field, save: &SaveRequested, window, cx| {
                let result = match owner {
                    Owner::Saves => Ok(save.value.clone()),
                    Owner::Refuses => Err(SaveFailure::Refused("The owner refused this save.".into())),
                    Owner::NeverAnswers => return,
                };
                field.update(cx, |field, cx| {
                    field.complete_save(save.request, result, window, cx);
                });
            })
        }));
        samples.into_iter().map(|(_, field)| field).collect()
    }

    fn whole_form(
        heard: &mut Vec<Subscription>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Entity<InlineForm> {
        let name = cx.new(|cx| InlineField::text("Name", "Workbox", window, cx).in_form().rule(required()));
        let email = cx.new(|cx| InlineField::email("Email", "name@example.com", window, cx).in_form());
        let status = cx.new(|cx| InlineField::chip("Status", statuses(), Some("todo".into()), window, cx).in_form());
        let due = cx.new(|cx| InlineField::date("Due", NaiveDate::from_ymd_opt(2026, 11, 12), window, cx).in_form());
        // Fails across fields: something done has to have a date.
        let rule: FormRule = Rc::new(|values| {
            let done = values.get("status") == Some(&FieldValue::Choice(Some("done".into())));
            if done && values.get("due") == Some(&FieldValue::Date(None)) {
                return vec![("due".into(), "Something done needs a date.".to_owned())];
            }
            Vec::new()
        });
        let form = cx.new(|cx| {
            InlineForm::new(
                vec![("name".into(), name), ("email".into(), email), ("status".into(), status), ("due".into(), due)],
                cx,
            )
            .rule(rule)
        });
        heard.push(cx.subscribe_in(&form, window, |_, form, save: &FormSaveRequested, window, cx| {
            form.update(cx, |form, cx| {
                form.complete_save(save.request, Ok(save.values.clone()), window, cx);
            });
        }));
        form
    }

    fn question_body(this: WeakEntity<Self>, answer: Entity<InputState>, fails: bool) -> CardBody {
        Rc::new(move |_, cx| {
            let this = this.clone();
            let failed = fails.then(|| this.upgrade().and_then(|this| this.read(cx).failed.clone())).flatten();
            v_flex()
                .gap_2()
                .child(Input::new(&answer).small())
                .child(
                    h_flex().gap_2().child(
                        Button::new(if fails { "gallery-send-fails" } else { "gallery-send" })
                            .small()
                            .primary()
                            .label("Send")
                            .on_click(move |_, _, cx| {
                                let _ = this.update(cx, |gallery, cx| gallery.send(fails, cx));
                            }),
                    ),
                )
                .children(failed.map(|failed| {
                    div().text_role(TextRole::Meta, cx).text_color(cx.theme().danger).child(failed)
                }))
                .into_any_element()
        })
    }

    fn send(&mut self, fails: bool, cx: &mut Context<Self>) {
        if fails {
            self.failed = Some("It couldn't be sent, so your answer is still here.".into());
        } else {
            self.answered = true;
            NeedsCard::settle(&NEED_KEYS[0].into(), &self.needs, cx);
        }
        cx.notify();
    }

    fn need_cards(&self, cx: &mut Context<Self>) -> Vec<AnyElement> {
        let this = cx.entity().downgrade();
        let answer = self.answer.clone();
        let opener = {
            let (drops, body) = (self.needs.clone(), Self::question_body(this.clone(), answer.clone(), false));
            LinkChip::new("gallery-open-link", IconName::ChevronDown, "Open from a link").on_click(move |_, _, cx| {
                NeedsCard::open(NEED_KEYS[0].into(), &drops, body.clone(), cx);
            })
        };
        let state = if self.answered {
            NeedsState::Answered { summary: "Answered: use main".into(), delivery: Some("Delivered to the session".into()) }
        } else {
            NeedsState::Open
        };
        vec![
            NeedsCard {
                key: NEED_KEYS[0].into(),
                kind: "Question".into(),
                title: "Which branch should this land on?".into(),
                why: "The worktree has two candidates, and the plan doesn't name one.".into(),
                next_step: Some("Then it opens the PR.".into()),
                chips: vec![StatusChip::new("TSK-1664", Tone::Neutral).into_any_element(), opener.into_any_element()],
                time: Some("2m".into()),
                state,
                drops: self.needs.clone(),
                body: Self::question_body(this.clone(), answer.clone(), false),
            }
            .into_any_element(),
            NeedsCard {
                key: NEED_KEYS[1].into(),
                kind: "Permission".into(),
                title: "Its answer fails to send".into(),
                why: "Send here fails, so the card stays open with the text you typed.".into(),
                next_step: None,
                chips: Vec::new(),
                time: Some("9m".into()),
                state: NeedsState::Open,
                drops: self.needs.clone(),
                body: Self::question_body(this, answer, true),
            }
            .into_any_element(),
        ]
    }

    fn sample_steps() -> Vec<StepView> {
        let step = |key: &'static str, mark, label: &'static str, right| StepView {
            key: key.into(),
            mark,
            label: label.into(),
            tool: None,
            detail: None,
            explanation: None,
            pills: Vec::new(),
            right,
            attempt: None,
            scope: None,
            closed: false,
        };
        vec![
            StepView {
                detail: Some("cargo test --locked inline_field".into()),
                tool: Some("Bash".into()),
                ..step("done", StepMark::Done, "Ran the tests", StepRight::Elapsed(format_elapsed(12_400)))
            },
            StepView {
                explanation: Some("A long explanation, which reads in one line here and in full once the row is opened, so a long one doesn't push the rows below it away until someone asks to read it.".into()),
                ..step("running", StepMark::Running, "Building the app", StepRight::Percent(40))
            },
            StepView {
                scope: Some(("repo:write".into(), false)),
                pills: vec![("Gate".into(), Tone::Accent)],
                ..step("waiting", StepMark::Waiting, "Waiting for a decision", StepRight::None)
            },
            StepView {
                attempt: Some((2, 3)),
                detail: Some("Exit code 101".into()),
                ..step("failed", StepMark::Failed, "Failed to push", StepRight::Elapsed(format_elapsed(320)))
            },
            step("upcoming", StepMark::Upcoming, "Open the PR", StepRight::None),
            StepView {
                closed: true,
                detail: Some("Nothing more will happen on this one.".into()),
                ..step("closed", StepMark::Upcoming, "A closed step", StepRight::Elapsed(format_elapsed(148_000)))
            },
        ]
    }

    fn run_rows(&self) -> Vec<AnyElement> {
        let run = |key: &'static str, title: &'static str, done, total, tone, state: &'static str| {
            let steps = self.steps.clone();
            RunRow {
                key: key.into(),
                title: title.into(),
                task_key: Some("TSK-1664".into()),
                step_line: state.into(),
                done,
                total,
                tone,
                pill: Some(StatusChip::new(state, tone).into_any_element()),
                body: Rc::new(move |_, cx| step_list(key, Self::sample_steps(), &steps, cx)),
                drops: self.runs.clone(),
            }
            .into_any_element()
        };
        vec![
            run("gallery-run-live", "Build the shared components", 2, 6, Tone::Info, "Running"),
            run("gallery-run-done", "A finished run", 6, 6, Tone::Success, "Succeeded"),
            run("gallery-run-failed", "A run that failed", 3, 6, Tone::Danger, "Failed"),
            run("gallery-run-empty", "A run with no steps yet", 0, 0, Tone::Neutral, "Waiting"),
        ]
    }

    fn session_rows(&self, cx: &mut Context<Self>) -> Vec<AnyElement> {
        let details = |task: &'static str, pr: &'static str, worktree: &'static str, project: &'static str| -> CardBody {
            Rc::new(move |_, _| {
                DescriptionList::new()
                    .columns(1)
                    .item("Task", task, 1)
                    .item("PR", pr, 1)
                    .item("Worktree", worktree, 1)
                    .item("Project", project, 1)
                    .into_any_element()
            })
        };
        let lead = |tone: Tone, cx: &App| div().size_2().flex_none().rounded_full().bg(tone.color(cx)).into_any_element();
        let rows = vec![
            SessionRowValues {
                key: "gallery-session-full".into(),
                lead: lead(Tone::Info, cx),
                title: "Shared components".into(),
                chips: vec![StatusChip::new("Claude", Tone::Accent).into_any_element()],
                task: Some("TSK-1664 Shared components".into()),
                pr: Some("PR 140".into()),
                worktree: Some("tsk-1664-components".into()),
                project: Some("Assembly".into()),
                step: Some("Building the app".into()),
                time: Some("2m".into()),
                body: details("TSK-1664 Shared components", "PR 140", "tsk-1664-components", "Assembly"),
            },
            SessionRowValues {
                key: "gallery-session-bare".into(),
                lead: lead(Tone::Neutral, cx),
                title: "A session with only a project".into(),
                chips: Vec::new(),
                task: None,
                pr: None,
                worktree: None,
                project: Some("Assembly".into()),
                step: None,
                time: None,
                body: details("None", "None", "None", "Assembly"),
            },
        ];
        rows.into_iter()
            .map(|values| {
                let key = values.key.clone();
                v_flex()
                    .w_full()
                    .child(session_row(values, &self.sessions, cx))
                    .child(GrowingBody::new(key, self.sessions.clone(), row_height(cx)).max_rows(None))
                    .into_any_element()
            })
            .collect()
    }
}

impl Render for ComponentGallery {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let muted = cx.theme().muted_foreground;
        let tones = [
            ("Neutral", Tone::Neutral),
            ("Info", Tone::Info),
            ("Success", Tone::Success),
            ("Warning", Tone::Warning),
            ("Danger", Tone::Danger),
            ("Accent", Tone::Accent),
        ];
        let chips = h_flex()
            .flex_wrap()
            .gap_2()
            .children(tones.map(|(label, tone)| StatusChip::new(label, tone)))
            .child(LinkChip::new("gallery-link", IconName::GitPullRequest, "PR 140"));
        div()
            .id("component-gallery")
            .size_full()
            .overflow_y_scroll()
            .track_focus(&self.surface)
            .child(
                v_flex()
                    .w_full()
                    .max_w(px(720.))
                    .p_4()
                    .gap_6()
                    .child(div().text_role(TextRole::Heading, cx).child("Components"))
                    .child(
                        div()
                            .text_role(TextRole::Body, cx)
                            .text_color(muted)
                            .child("Every shared piece, on sample data held in memory. Text follows the interface size and colours follow the theme in Settings."),
                    )
                    .child(ActivitySection::new("gallery-chips", "Chips").child(chips))
                    .child(
                        ActivitySection::new("gallery-fields", "Fields that save themselves")
                            .subtitle("Enter or leaving saves, a pick saves, and Escape restores.")
                            .count(self.fields.len())
                            .children(self.fields.iter().cloned()),
                    )
                    .child(
                        ActivitySection::new("gallery-form", "Fields that save together")
                            .subtitle("One Save, all or nothing. Something Done needs a date.")
                            .child(self.form.clone()),
                    )
                    .child({
                        let cards = self.need_cards(cx);
                        ActivitySection::new("gallery-needs", "Needs cards")
                            .tone(Tone::Warning)
                            .count(cards.len())
                            .children(cards)
                    })
                    .child(
                        ActivitySection::new("gallery-steps", "Step list")
                            .tone(Tone::Info)
                            .child(step_list("gallery-steps", Self::sample_steps(), &self.steps, cx)),
                    )
                    .child({
                        let rows = self.run_rows();
                        ActivitySection::new("gallery-runs", "Run rows").tone(Tone::Info).count(rows.len()).children(rows)
                    })
                    .child({
                        let rows = self.session_rows(cx);
                        ActivitySection::new("gallery-sessions", "Session rows")
                            .action(Button::new("gallery-action").ghost().xsmall().label("An action"))
                            .count(rows.len())
                            .children(rows)
                    }),
            )
    }
}

#[cfg(test)]
#[path = "component_gallery_tests.rs"]
mod tests;

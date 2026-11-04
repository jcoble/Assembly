//! The gallery's run and session rows.
use super::*;

impl ComponentGallery {
    pub(super) fn sample_steps() -> Vec<StepView> {
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

    pub(super) fn run_rows(&self) -> Vec<AnyElement> {
        let run =
            |key: &'static str, title: &'static str, done, total, tone, state: &'static str| {
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
            run(
                "gallery-run-live",
                "Build the shared components",
                2,
                6,
                Tone::Info,
                "Running",
            ),
            run(
                "gallery-run-done",
                "A finished run",
                6,
                6,
                Tone::Success,
                "Succeeded",
            ),
            run(
                "gallery-run-failed",
                "A run that failed",
                3,
                6,
                Tone::Danger,
                "Failed",
            ),
            run(
                "gallery-run-empty",
                "A run with no steps yet",
                0,
                0,
                Tone::Neutral,
                "Waiting",
            ),
        ]
    }

    pub(super) fn session_rows(&self, cx: &mut Context<Self>) -> Vec<AnyElement> {
        let details = |task: &'static str,
                       pr: &'static str,
                       worktree: &'static str,
                       project: &'static str|
         -> CardBody {
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
        let lead = |tone: Tone, cx: &App| {
            div()
                .size_2()
                .flex_none()
                .rounded_full()
                .bg(tone.color(cx))
                .into_any_element()
        };
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
                body: details(
                    "TSK-1664 Shared components",
                    "PR 140",
                    "tsk-1664-components",
                    "Assembly",
                ),
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
                    .child(
                        GrowingBody::new(key, self.sessions.clone(), row_height(cx)).max_rows(None),
                    )
                    .into_any_element()
            })
            .collect()
    }
}

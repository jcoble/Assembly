//! How the deploy dialog draws: what this is and what it does to the machine, the bundle, the machine, and how the install
//! is going. Everything stays in one column, and the Deploy button says which backend it restarts.

use crate::core::theme::text::{TextRole, TextRoleExt as _};
use gpui_kit::assets::IconName as Glyph;
use gpui_kit::component::alert::Alert;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::input::Input;
use gpui_kit::component::menu::{DropdownMenu as _, PopupMenuItem};
use gpui_kit::component::progress::Progress;
use gpui_kit::component::{ActiveTheme as _, Disableable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::deploy_form::DeployForm;
use super::job::{BundleFacts, Ending, Job, JobState, size_words, state_words};

fn fact(label: &str, value: impl Into<SharedString>, cx: &App) -> Div {
    h_flex()
        .gap_3()
        .text_role(TextRole::Body, cx)
        .child(
            div()
                .w(px(80.))
                .flex_none()
                .text_color(cx.theme().muted_foreground)
                .child(label.to_string()),
        )
        .child(div().min_w_0().child(value.into()))
}

fn bundle_facts(facts: &BundleFacts, cx: &App) -> Div {
    let hash: String = facts.sha256.chars().take(16).collect();
    v_flex()
        .gap_1()
        .child(fact("Build", "DEVELOPMENT".to_string(), cx))
        .child(fact("Commit", facts.commit.clone(), cx))
        .child(fact("Target", facts.target.clone(), cx))
        .child(fact(
            "Protocol",
            format!("{}, format {}", facts.protocol, facts.format),
            cx,
        ))
        .child(fact("Size", size_words(facts.bytes), cx))
        .child(fact("SHA-256", format!("{hash}…"), cx))
}

impl DeployForm {
    fn bundle_section(&self, cx: &mut Context<Self>) -> Div {
        let held = self.occupied();
        let path = !self.path_text(cx).is_empty();
        let theme = cx.theme();
        v_flex()
            .gap_2()
            .child(
                div()
                    .text_role(TextRole::Body, cx)
                    .font_weight(FontWeight::MEDIUM)
                    .child("Bundle on this computer"),
            )
            .child(
                h_flex()
                    .gap_2()
                    .child(
                        div()
                            .flex_1()
                            .min_w_0()
                            .child(Input::new(&self.path).disabled(held)),
                    )
                    .child(
                        Button::new("remote-install:choose")
                            .outline()
                            .label("Choose…")
                            .disabled(held)
                            .on_click(
                                cx.listener(|form, _, window, cx| form.choose_file(window, cx)),
                            ),
                    )
                    .child(
                        Button::new("remote-install:inspect")
                            .outline()
                            .label(if self.inspecting {
                                "Inspecting…"
                            } else {
                                "Inspect"
                            })
                            .disabled(held || !path || self.inspecting)
                            .on_click(cx.listener(|form, _, _, cx| form.inspect(cx))),
                    ),
            )
            .when_some(self.inspect_error.clone(), |section, error| {
                section.child(
                    div()
                        .text_role(TextRole::Body, cx)
                        .text_color(theme.danger)
                        .child(error),
                )
            })
            .when_some(self.facts.as_ref(), |section, (facts, _, _)| {
                section
                    .child(bundle_facts(facts, cx))
                    .when_some(facts.refusal(), |section, why| {
                        section.child(
                            div()
                                .text_role(TextRole::Body, cx)
                                .text_color(theme.danger)
                                .child(why),
                        )
                    })
            })
    }

    fn machine_section(&self, cx: &mut Context<Self>) -> Div {
        let picked = self.machine.clone();
        let held = self.occupied();
        let machines = self.machines(cx);
        let form = cx.weak_entity();
        let theme = cx.theme();
        let listed = picked.clone();
        let words = if self.resolving {
            "Asking the machine which Engine it is…".to_string()
        } else {
            match &self.target {
                Some(target) => format!("Engine {}", target.engine),
                None => String::new(),
            }
        };
        v_flex()
            .gap_2()
            .child(
                div()
                    .text_role(TextRole::Body, cx)
                    .font_weight(FontWeight::MEDIUM)
                    .child("Test machine"),
            )
            .child(
                h_flex()
                    .gap_3()
                    .child(
                        Button::new("remote-install:machine")
                            .outline()
                            .label(picked.clone().unwrap_or_else(|| {
                                if machines.is_empty() {
                                    "No saved remote machines".to_string()
                                } else {
                                    "Choose a machine".to_string()
                                }
                            }))
                            .dropdown_caret(true)
                            .disabled(held || machines.is_empty())
                            .dropdown_menu_with_anchor(Anchor::TopLeft, move |mut menu, _, _| {
                                for machine in machines.clone() {
                                    let form = form.clone();
                                    let checked = listed.as_ref() == Some(&machine);
                                    menu = menu.item(
                                        PopupMenuItem::new(machine.clone())
                                            .checked(checked)
                                            .on_click(move |_, _, cx| {
                                                _ = form.update(cx, |form, cx| {
                                                    form.pick_machine(machine.clone(), cx)
                                                });
                                            }),
                                    );
                                }
                                menu
                            }),
                    )
                    .child(
                        div()
                            .text_role(TextRole::Body, cx)
                            .text_color(theme.muted_foreground)
                            .child(words),
                    ),
            )
            .when_some(self.target_error.clone(), |section, error| {
                section.child(
                    h_flex()
                        .gap_2()
                        .text_role(TextRole::Body, cx)
                        .text_color(theme.danger)
                        .child(error)
                        .child(
                            Button::new("remote-install:target-again")
                                .ghost()
                                .compact()
                                .label("Try again")
                                .on_click(cx.listener(|form, _, _, cx| form.resolve_target(cx))),
                        ),
                )
            })
    }

    fn job_section(&self, cx: &mut Context<Self>) -> Option<Div> {
        let job = self.job.as_ref();
        if job.is_none() && self.expected.is_none() {
            return None;
        }
        let theme = cx.theme();
        let (danger, muted, success) = (theme.danger, theme.muted_foreground, theme.success);
        let ending = job.and_then(|job| job.ending(self.expected.as_ref()));
        let headline = match (&ending, job) {
            (Some(Ending::Confirmed(result)), _) => {
                format!("Deployed: {} now runs {} on Engine {}.", result.machine, result.commit, result.engine)
            }
            (Some(Ending::Unproven), _) => {
                "The backend reported success, but its result doesn't match this deploy. Check the machine.".to_string()
            }
            (None, Some(job)) => state_words(job),
            (None, None) => "Waiting for the backend".to_string(),
        };
        let bad = matches!(ending, Some(Ending::Unproven))
            || job.is_some_and(|job| matches!(job.state, JobState::Failed | JobState::Uncertain));
        let color = if bad {
            danger
        } else if matches!(ending, Some(Ending::Confirmed(_))) {
            success
        } else {
            theme.foreground
        };
        let live = job.is_some_and(|job| !job.settled());
        Some(
            v_flex()
                .gap_2()
                .child(
                    div()
                        .text_role(TextRole::Body, cx)
                        .font_weight(FontWeight::MEDIUM)
                        .child("Install"),
                )
                .child(
                    div()
                        .text_role(TextRole::Body, cx)
                        .text_color(color)
                        .child(headline),
                )
                .when_some(
                    job.filter(|_| live).and_then(Job::percent),
                    |section, percent| {
                        section.child(Progress::new("remote-install:progress").value(percent))
                    },
                )
                .when_some(
                    job.filter(|job| live && job.total_bytes > 0),
                    |section, job| {
                        section.child(div().text_role(TextRole::Meta, cx).text_color(muted).child(
                            format!(
                                "{} of {}. Closing this dialog doesn't stop it.",
                                size_words(job.bytes_done),
                                size_words(job.total_bytes)
                            ),
                        ))
                    },
                )
                .when_some(
                    job.and_then(|job| job.error_code.clone()),
                    |section, code| {
                        section.child(
                            div()
                                .text_role(TextRole::Meta, cx)
                                .text_color(muted)
                                .child(format!("Code: {code}")),
                        )
                    },
                )
                .when_some(self.unknown.clone(), |section, why| {
                    section.child(
                        div()
                            .text_role(TextRole::Body, cx)
                            .text_color(danger)
                            .child(why),
                    )
                })
                .when_some(self.cancel_error.clone(), |section, why| {
                    section.child(
                        div()
                            .text_role(TextRole::Body, cx)
                            .text_color(danger)
                            .child(why),
                    )
                }),
        )
    }

    fn actions(&self, cx: &mut Context<Self>) -> Div {
        let can_deploy = self.ready(cx).is_some();
        let machine = self
            .machine
            .clone()
            .unwrap_or_else(|| "the machine".to_string());
        let can_cancel = self.job.as_ref().is_some_and(Job::cancellable);
        let settled = self.job.as_ref().is_some_and(Job::settled);
        let lost = self.unknown.is_some();
        h_flex()
            .justify_end()
            .gap_2()
            .when(lost, |row| {
                row.child(
                    Button::new("remote-install:check")
                        .ghost()
                        .label("Check again")
                        .on_click(cx.listener(|form, _, _, cx| form.check_again(cx))),
                )
            })
            .when(lost || settled, |row| {
                row.child(
                    Button::new("remote-install:forget")
                        .ghost()
                        .label("Start over")
                        .on_click(cx.listener(|form, _, _, cx| form.forget(cx))),
                )
            })
            .when(can_cancel, |row| {
                row.child(
                    Button::new("remote-install:cancel")
                        .outline()
                        .label(if self.cancelling {
                            "Cancelling…"
                        } else {
                            "Cancel install"
                        })
                        .disabled(self.cancelling)
                        .on_click(cx.listener(|form, _, _, cx| form.cancel(cx))),
                )
            })
            .when(!settled, |row| {
                row.child(
                    Button::new("remote-install:deploy")
                        .danger()
                        .icon(Glyph::Upload)
                        .label(format!("Deploy and restart {machine}"))
                        .disabled(!can_deploy)
                        .on_click(cx.listener(|form, _, _, cx| form.deploy(cx))),
                )
            })
    }
}

impl Render for DeployForm {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let start_error = self.start_error.clone();
        let danger = cx.theme().danger;
        v_flex()
            .gap_4()
            .child(
                Alert::warning(
                    "remote-install:warning",
                    "This restarts the test backend, which can interrupt work running there. It's a temporary \
                     development tool for the owned test machine, not a production update.",
                )
                .title("DEVELOPMENT"),
            )
            .child(self.bundle_section(cx))
            .child(self.machine_section(cx))
            .children(self.job_section(cx))
            .when_some(start_error, |form, error| {
                form.child(div().text_role(TextRole::Body, cx).text_color(danger).child(error))
            })
            .child(self.actions(cx))
    }
}

//! The Checks page: every check grouped by its workflow run, failures first. A failed Actions job opens to the part of
//! its log that failed, with the failed step named, so the person can see why without the website; from there the
//! agent can be asked to fix it, and the run's failed jobs can run again.
use super::super::{
    pr_activity::{capitalized, check_summary, check_tone, check_word, skipped},
    pr_writes::PrAct,
    pull_request::{CheckTone, PrCheckLog, PullRequestCheck},
};
use super::lookups::{Looked, PrLookups};
use super::summary::quiet;
use super::*;
use crate::activity::{StatusChip, Tone};
use crate::core::ui::row_drop_down::GrowingBody;
use crate::core::ui::spinner::Spinner;
use crate::core::ui::three_line_row::row_height;
use crate::section_card::SectionCard;
use gpui_kit::component::Icon;
use gpui_kit::component::scroll::ScrollableElement as _;

/// The log lines an Ask agent prompt carries; the agent can read the rest itself.
const PROMPT_LINES: usize = 80;

fn tone_of(check: &PullRequestCheck) -> Tone {
    if skipped(check) {
        return Tone::Neutral;
    }
    match check_tone(check) {
        CheckTone::Passing => Tone::Success,
        CheckTone::Failing => Tone::Danger,
        _ => Tone::Warning,
    }
}
/// How long a finished check took, as "1m 12s".
fn took(check: &PullRequestCheck) -> Option<String> {
    let at = |stamp: &str| chrono::DateTime::parse_from_rfc3339(stamp).ok();
    let seconds = (at(&check.completed_at)? - at(&check.started_at)?).num_seconds();
    (seconds >= 0).then(|| match seconds {
        0..60 => format!("{seconds}s"),
        60..3600 => format!("{}m {}s", seconds / 60, seconds % 60),
        _ => format!("{}h {}m", seconds / 3600, seconds % 3600 / 60),
    })
}
/// The checks in their runs: each group is a workflow run, or one check outside Actions. Groups with a failure come
/// first, then running ones, then the rest, each in GitHub's order.
pub(super) fn check_groups(
    checks: &[PullRequestCheck],
) -> Vec<(String, Option<u64>, Vec<&PullRequestCheck>)> {
    let mut groups: Vec<(String, Option<u64>, Vec<&PullRequestCheck>)> = Vec::new();
    for check in checks {
        let title = if check.workflow.is_empty() {
            check.name.clone()
        } else {
            check.workflow.clone()
        };
        match groups
            .iter_mut()
            .find(|(name, run, _)| check.run_id.is_some() && *run == check.run_id && *name == title)
        {
            Some((_, _, members)) => members.push(check),
            None => groups.push((title, check.run_id, vec![check])),
        }
    }
    let rank = |members: &Vec<&PullRequestCheck>| {
        let tones: Vec<_> = members.iter().map(|check| check_tone(check)).collect();
        if tones.contains(&CheckTone::Failing) {
            0
        } else if tones.contains(&CheckTone::Pending) {
            1
        } else {
            2
        }
    };
    groups.sort_by_key(|(_, _, members)| rank(members));
    groups
}
/// What the agent is asked when a check failed: the check, the step, and the end of the failing part of its log.
pub(super) fn fix_prompt(
    detail: &PullRequestDetail,
    check: &PullRequestCheck,
    log: &PrCheckLog,
) -> String {
    let start = log.lines.len().saturating_sub(PROMPT_LINES);
    let step = if log.step.is_empty() {
        String::new()
    } else {
        format!("\nFailed step: {}", log.step)
    };
    format!(
        "The \"{}\" check failed on pull request #{} ({}, branch {}).{step}\n\nThe failing part of its log:\n```\n{}\n```\n\nFind the cause, fix it on this branch and push. The full log: {}",
        check.name,
        detail.number,
        detail.repository,
        detail.head_branch,
        log.lines[start..].join("\n"),
        check.url
    )
}
fn log_body(
    lookups: &Entity<PrLookups>,
    detail: &WeakEntity<PrDetail>,
    shown: &PullRequestDetail,
    check: &PullRequestCheck,
    cx: &App,
) -> AnyElement {
    let theme = cx.theme();
    let (danger, warning, muted, mono, border) = (
        theme.danger,
        theme.warning,
        theme.muted_foreground,
        theme.mono_font_family.clone(),
        theme.border,
    );
    let Some(job) = check.job_id else {
        return Empty.into_any_element();
    };
    let column = v_flex().gap_2().px_3().pb_3().pt_1();
    match lookups.read(cx).log(job, cx) {
        None | Some(Looked::Reading) => column
            .child(
                h_flex()
                    .gap_2()
                    .text_role(TextRole::Body, cx)
                    .text_color(muted)
                    .child(Spinner::new().small())
                    .child("Reading the log…"),
            )
            .into_any_element(),
        Some(Looked::Failed(error)) => column
            .child(
                div()
                    .text_role(TextRole::Body, cx)
                    .text_color(danger)
                    .child(error),
            )
            .into_any_element(),
        Some(Looked::Found(log)) => {
            let prompt = fix_prompt(shown, check, &log);
            let target = detail.clone();
            let url = check.url.clone();
            column
                .when(!log.step.is_empty(), |column| {
                    column.child(
                        h_flex()
                            .gap_2()
                            .text_role(TextRole::Body, cx)
                            .child(
                                Icon::new(IconName::CircleX)
                                    .size(px(14.))
                                    .text_color(danger),
                            )
                            .child(div().text_color(muted).child("Failed at"))
                            .child(
                                div()
                                    .font_weight(FontWeight::MEDIUM)
                                    .child(log.step.clone()),
                            ),
                    )
                })
                .child(
                    v_flex()
                        .id(SharedString::from(format!("pr-log:{job}")))
                        .max_h(px(360.))
                        .overflow_scroll()
                        .p_2()
                        .rounded(px(6.))
                        .border_1()
                        .border_color(border)
                        .font_family(mono)
                        .text_role(TextRole::Meta, cx)
                        .when(log.cut, |lines| {
                            lines.child(div().text_color(muted).child("… earlier lines left out"))
                        })
                        .children(log.lines.iter().map(|line| {
                            let color = if line.starts_with("Error:") {
                                Some(danger)
                            } else if line.starts_with("Warning:") {
                                Some(warning)
                            } else {
                                None
                            };
                            div()
                                .whitespace_nowrap()
                                .when_some(color, |line, color| line.text_color(color))
                                .child(if line.is_empty() {
                                    " ".to_string()
                                } else {
                                    line.clone()
                                })
                        })),
                )
                .child(
                    h_flex()
                        .gap_2()
                        .child(
                            Button::new(SharedString::from(format!("pr-log-ask:{job}")))
                                .small()
                                .icon(IconName::Bot)
                                .label("Ask an agent to fix it")
                                .on_click(move |_, window, cx| {
                                    _ = target.update(cx, |view, cx| {
                                        view.ask_agent(prompt.clone(), window, cx)
                                    });
                                }),
                        )
                        .child(
                            Button::new(SharedString::from(format!("pr-log-web:{job}")))
                                .ghost()
                                .small()
                                .icon(IconName::ExternalLink)
                                .label("Full log on GitHub")
                                .on_click(move |_, window, cx| {
                                    window.dispatch_action(
                                        Box::new(OpenInBrowser { url: url.clone() }),
                                        cx,
                                    )
                                }),
                        ),
                )
                .into_any_element()
        }
    }
}
impl PrDetail {
    fn toggle_log(&mut self, check: &PullRequestCheck, cx: &mut Context<Self>) {
        let (Some(job), Some(detail)) = (check.job_id, self.detail.clone()) else {
            return;
        };
        let number = self.number;
        self.lookups
            .update(cx, |lookups, cx| lookups.read_log(number, job, cx));
        let (lookups, target, check) =
            (self.lookups.clone(), cx.entity().downgrade(), check.clone());
        self.drops.update(cx, |drops, cx| {
            drops.toggle(
                SharedString::from(format!("check:{job}")),
                move |_, cx| log_body(&lookups, &target, &detail, &check, cx),
                cx,
            )
        });
    }
    fn rerun(&mut self, run: u64, cx: &mut Context<Self>) {
        self.act(PrAct::Rerun, json!({"runId":run}), cx);
    }
    fn check_line(&self, check: &PullRequestCheck, cx: &mut Context<Self>) -> AnyElement {
        let tone = tone_of(check);
        let failing = check_tone(check) == CheckTone::Failing;
        let opens = failing && check.job_id.is_some();
        let key = SharedString::from(format!("check:{}", check.job_id.unwrap_or_default()));
        let open = opens && self.drops.read(cx).is_open(&key);
        let muted = cx.theme().muted_foreground;
        let hover = cx.theme().secondary_hover;
        let url = check.url.clone();
        let target = check.clone();
        let mark = match check_tone(check) {
            // A job that didn't run is an empty ring.
            _ if skipped(check) => div()
                .size(px(13.))
                .m(px(1.))
                .rounded_full()
                .border_1()
                .border_color(muted)
                .into_any_element(),
            CheckTone::Passing => Icon::new(IconName::CircleCheck)
                .size(px(15.))
                .text_color(tone.color(cx))
                .into_any_element(),
            CheckTone::Failing => Icon::new(IconName::CircleX)
                .size(px(15.))
                .text_color(tone.color(cx))
                .into_any_element(),
            _ => Spinner::new()
                .small()
                .color(tone.color(cx))
                .into_any_element(),
        };
        let line = h_flex()
            .id(SharedString::from(format!(
                "pr-check-line:{}:{}",
                check.name,
                check.job_id.unwrap_or_default()
            )))
            .gap_2()
            .px_3()
            .py(px(7.))
            .rounded(px(6.))
            .text_role(TextRole::Body, cx)
            .hover(move |line| line.bg(hover))
            .child(mark)
            .child(
                div()
                    .flex_1()
                    .min_w_0()
                    .truncate()
                    .child(if check.name.is_empty() {
                        "Check".to_string()
                    } else {
                        check.name.clone()
                    }),
            )
            .children(took(check).map(|took| {
                div()
                    .flex_none()
                    .text_role(TextRole::Meta, cx)
                    .text_color(muted)
                    .child(took)
            }))
            .child(
                div()
                    .flex_none()
                    .w(px(84.))
                    .flex()
                    .justify_end()
                    .child(StatusChip::new(capitalized(&check_word(check)), tone)),
            )
            .child(div().flex_none().w(px(14.)).when(opens, |slot| {
                slot.child(
                    Icon::new(if open {
                        IconName::ChevronDown
                    } else {
                        IconName::ChevronRight
                    })
                    .size(px(14.))
                    .text_color(muted),
                )
            }))
            .cursor_pointer()
            .on_click(cx.listener(move |view, _, window, cx| {
                if opens {
                    view.toggle_log(&target, cx);
                } else if !url.is_empty() {
                    window.dispatch_action(Box::new(OpenInBrowser { url: url.clone() }), cx);
                }
            }));
        v_flex()
            .child(line)
            .when(opens, |row| {
                row.child(GrowingBody::new(key, self.drops.clone(), row_height(cx)).max_rows(None))
            })
            .into_any_element()
    }
    pub(super) fn checks_page(
        &self,
        detail: &PullRequestDetail,
        cx: &mut Context<Self>,
    ) -> AnyElement {
        let acting = self.writes.read(cx).acting(cx);
        let free = self.writes_free(cx) && self.current_identity().is_some();
        let groups = check_groups(&detail.checks);
        let page = v_flex().gap_3().p_5().max_w(px(960.)).child(
            h_flex()
                .gap_2()
                .text_role(TextRole::Title, cx)
                .font_weight(FontWeight::SEMIBOLD)
                .child(check_summary(&detail.checks))
                .child(div().flex_1())
                .children((detail.state == "OPEN").then(|| {
                    quiet(
                        format!("at {}", detail.head_sha.chars().take(7).collect::<String>()),
                        cx,
                    )
                })),
        );
        let page = if groups.is_empty() {
            page.child(quiet(
                "GitHub reports no checks for this pull request's head.",
                cx,
            ))
        } else {
            page.children(groups.into_iter().map(|(title, run, members)| {
                let failed = members
                    .iter()
                    .any(|check| check_tone(check) == CheckTone::Failing);
                let run_url = members
                    .first()
                    .map(|check| check.url.clone())
                    .unwrap_or_default();
                let run_url = run_url
                    .split("/job/")
                    .next()
                    .unwrap_or_default()
                    .to_string();
                let rerun = run.filter(|_| failed).map(|run| {
                    Button::new(SharedString::from(format!("pr-rerun:{run}")))
                        .outline()
                        .small()
                        .icon(IconName::RefreshCw)
                        .label(if acting == Some(PrAct::Rerun) {
                            PrAct::Rerun.doing()
                        } else {
                            "Re-run failed jobs"
                        })
                        .loading(acting == Some(PrAct::Rerun))
                        .disabled(!free)
                        .on_click(cx.listener(move |view, _, _, cx| view.rerun(run, cx)))
                });
                let web = (run.is_some() && !run_url.is_empty()).then(|| {
                    let url = run_url.clone();

                    Button::new(SharedString::from(format!(
                        "pr-run-web:{}",
                        run.unwrap_or_default()
                    )))
                    .ghost()
                    .small()
                    .icon(IconName::ExternalLink)
                    .label("Run on GitHub")
                    .on_click(move |_, window, cx| {
                        window.dispatch_action(Box::new(OpenInBrowser { url: url.clone() }), cx)
                    })
                });
                let card = SectionCard::new(IconName::ListChecks, title);
                let card = match rerun {
                    Some(rerun) => card.action(rerun),
                    None => card,
                };
                let card = match web {
                    Some(web) => card.action(web),
                    None => card,
                };
                card.children(members.into_iter().map(|check| self.check_line(check, cx)))
            }))
        };
        div()
            .id("pr-checks-page")
            .flex_1()
            .min_h_0()
            .overflow_y_scrollbar()
            .child(page)
            .into_any_element()
    }
}

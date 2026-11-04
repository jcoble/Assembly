//! What the pane's last action came to: a muted line that goes on the next read, or a plain sentence with git's text
//! under Details and the next step where one is known (g9#10).
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use super::git_failure::{GitFailure, Recovery};
use super::source_control_pane::SourceControlPane;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::collapsible::Collapsible;
use gpui_kit::component::{ActiveTheme as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

#[derive(Clone, Debug, PartialEq)]
pub enum PaneResult {
    Done(String),
    Failed(GitFailure),
}
pub fn recovery_label(recovery: &Recovery) -> &'static str {
    match recovery {
        Recovery::Refresh => "Refresh",
        Recovery::Connect(_) => "Connect",
        Recovery::ReconcileOperation => "Refresh operation status",
    }
}
pub fn result_line(
    result: &PaneResult,
    details_open: bool,
    cx: &mut Context<SourceControlPane>,
) -> AnyElement {
    let muted = cx.theme().muted_foreground;
    match result {
        PaneResult::Done(text) => div()
            .text_role(TextRole::Body, cx)
            .text_color(muted)
            .child(text.clone())
            .into_any_element(),
        PaneResult::Failed(failure) => {
            let details = failure.details.clone();
            let recovery = failure.recovery.clone();
            v_flex()
                .gap_1()
                .child(
                    div()
                        .text_role(TextRole::Body, cx)
                        .text_color(cx.theme().danger)
                        .child(failure.sentence.clone()),
                )
                .when(details.is_some() || recovery.is_some(), |line| {
                    line.child(
                        h_flex()
                            .gap_1()
                            .when(details.is_some(), |row| {
                                row.child(
                                    Button::new("git-result-details")
                                        .ghost()
                                        .compact()
                                        .label("Details")
                                        .on_click(cx.listener(|pane, _, _, cx| {
                                            pane.toggle_details(cx)
                                        })),
                                )
                            })
                            .when_some(recovery, |row, recovery| {
                                row.child(
                                    Button::new("git-result-recovery")
                                        .ghost()
                                        .compact()
                                        .label(recovery_label(&recovery))
                                        .on_click(cx.listener(move |pane, _, _, cx| {
                                            pane.recover(recovery.clone(), cx)
                                        })),
                                )
                            }),
                    )
                })
                .when_some(details, |line, details| {
                    line.child(
                        Collapsible::new().open(details_open).content(
                            div().text_role(TextRole::Body, cx).text_color(muted).child(details),
                        ),
                    )
                })
                .into_any_element()
        }
    }
}

//! `ToolRow` draws a tool call: its mark (running, done or failed), title, summary and line counts, and, opened, its
//! command, output tail and diff from `rows/tool`. The detail is read when it opens and again only when the call's
//! state changes (an edit's diff once the edit finishes). A long diff shows its first lines until shown in full.
//! Replaces chat/Tool.svelte and chat/tools/tool.ts.

use gpui_kit::component::{ActiveTheme as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::grow::{band, chevron, faded, grow, turn};
use super::session_action::{OnAction, SessionAction};
use crate::features::session::model::fold::Fold;
use crate::features::session::model::kept::Kept;
use crate::features::session::rows::tool::{ToolDetail, ToolPayload, ToolState};

/// A diff past this many lines shows its first `DIFF_SHOWN` until shown in full.
pub const DIFF_FOLD: usize = 20;
pub const DIFF_SHOWN: usize = 12;
/// The most output lines an open tool shows, from its end.
pub const OUTPUT_TAIL: usize = 200;

#[derive(IntoElement)]
pub struct ToolRow {
    /// The item's key, which its toggle sends.
    pub key: String,
    pub tool: ToolPayload,
    pub kept: Kept,
    pub on_action: OnAction,
}

/// The diff's lines to show, and how many are hidden.
pub fn diff_lines(diff: &str, all: bool) -> (Vec<&str>, usize) {
    let lines: Vec<&str> = diff.lines().collect();
    if all || lines.len() <= DIFF_FOLD {
        return (lines, 0);
    }
    let hidden = lines.len() - DIFF_SHOWN;
    (lines[..DIFF_SHOWN].to_vec(), hidden)
}

/// The output's last `OUTPUT_TAIL` lines.
pub fn output_tail(output: &str) -> &str {
    let mut at = output.len();
    let body = output.strip_suffix('\n').unwrap_or(output);
    for (count, (ix, _)) in body.rmatch_indices('\n').enumerate() {
        if count + 1 == OUTPUT_TAIL {
            at = ix + 1;
            break;
        }
    }
    if at == output.len() {
        output
    } else {
        &output[at..]
    }
}

impl RenderOnce for ToolRow {
    fn render(self, window: &mut Window, cx: &mut App) -> impl IntoElement {
        let theme = cx.theme();
        let (muted, success, danger) = (theme.muted_foreground, theme.success, theme.danger);
        let mark = match self.tool.state {
            ToolState::Started => "…",
            ToolState::Completed => "✓",
            ToolState::Failed => "✗",
        };
        let open = self.kept.open && self.tool.has_detail;
        let toggled_at = self.kept.toggled_at;
        let chevron = self
            .tool
            .has_detail
            .then(|| chevron(turn(open, toggled_at, window, cx).0));
        let turned = turn(open, self.kept.grown_at(), window, cx);
        let faded = faded(turned.0);
        let theme = cx.theme();
        let (key, on_action) = (self.key.clone(), self.on_action.clone());
        let line = h_flex()
            .id(SharedString::from(format!("{}:line", self.key)))
            .gap_2()
            .text_sm()
            .text_color(muted)
            .when(self.tool.has_detail, |line| {
                line.cursor_pointer().on_click(move |_, window, cx| {
                    on_action(SessionAction::Toggle { key: key.clone() }, window, cx)
                })
            })
            .children(chevron)
            .child(mark)
            .child(
                div()
                    .font_weight(FontWeight::MEDIUM)
                    .text_color(theme.foreground)
                    .child(Fold::tool_title(&self.tool.name)),
            )
            .child(
                div()
                    .flex_1()
                    .min_w_0()
                    .truncate()
                    .child(self.tool.summary.clone().unwrap_or_default()),
            )
            .when_some(self.tool.added.filter(|n| *n > 0), |line, added| {
                line.child(div().text_color(success).child(format!("+{added}")))
            })
            .when_some(self.tool.removed.filter(|n| *n > 0), |line, removed| {
                line.child(div().text_color(danger).child(format!("-{removed}")))
            });
        let detail = self
            .kept
            .detail
            .as_ref()
            .filter(|_| open || turned.1)
            .map(|(_, detail)| self.detail(detail, cx));
        let detail = detail.map(|detail| {
            let id = format!("{}:grow", self.key).into();
            grow(id, turned, open, faded, detail, window, cx)
        });
        let column = v_flex().gap_1().child(line).children(detail);
        band(column, turned.0, cx)
    }
}

impl ToolRow {
    fn detail(&self, detail: &ToolDetail, cx: &App) -> Div {
        let theme = cx.theme();
        let block = || {
            div()
                .p_2()
                .rounded(theme.radius)
                .bg(theme.muted)
                .font_family(theme.mono_font_family.clone())
                .text_xs()
                .overflow_x_hidden()
        };
        let mut column = v_flex().gap_1().pl_5();
        if let Some(input) = detail.input.as_deref().filter(|input| !input.is_empty()) {
            column = column.child(block().child(input.to_string()));
        }
        if let Some(output) = detail.output.as_deref().filter(|output| !output.is_empty()) {
            column = column.child(block().child(output_tail(output).to_string()));
        }
        if let Some(diff) = detail.diff.as_deref().filter(|diff| !diff.is_empty()) {
            let (lines, hidden) = diff_lines(diff, self.kept.all_diff);
            let (added, removed) = (theme.success, theme.danger);
            let mut shown = block();
            for line in lines {
                let color = match line.chars().next() {
                    Some('+') => Some(added),
                    Some('-') => Some(removed),
                    _ => None,
                };
                shown = shown.child(
                    div()
                        .when_some(color, |line, color| line.text_color(color))
                        .child(line.to_string()),
                );
            }
            column = column.child(shown);
            if hidden > 0 {
                let (key, on_action) = (self.key.clone(), self.on_action.clone());
                column = column.child(
                    div()
                        .id(SharedString::from(format!("{}:all-diff", self.key)))
                        .text_xs()
                        .text_color(theme.link)
                        .cursor_pointer()
                        .on_click(move |_, window, cx| {
                            on_action(SessionAction::ShowAllDiff { key: key.clone() }, window, cx)
                        })
                        .child(format!("Show {hidden} more lines")),
                );
            }
        }
        if detail.truncated {
            column = column.child(
                div()
                    .text_xs()
                    .text_color(theme.muted_foreground)
                    .child("The backend cut this output short."),
            );
        }
        column
    }
}

#[cfg(test)]
#[path = "tool_row_tests.rs"]
mod tests;

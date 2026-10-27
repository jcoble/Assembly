//! `ToolRow` draws a tool call: its step line (`StepRow`: sign, title, summary, line counts), and, opened, the
//! command it ran, its output tail and diff from `rows/tool`. The detail is read when it opens and again only when the
//! call's state changes (an edit's diff once the edit finishes). A long diff shows its first lines until shown in
//! full. Replaces chat/Tool.svelte and chat/tools/tool.ts.

use gpui_kit::component::{ActiveTheme as _, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::grow::{band, grow, turn};
use super::session_action::{OnAction, SessionAction};
use super::step_row::{OnClick, StepRow, StepState};
use crate::core::theme::grow::faded;
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
    /// It's in the running turn's activity card, with how long it has run: only there does a started call spin.
    pub took_ms: Option<i64>,
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

/// The command a call ran, from its arguments' JSON: what an open tool shows of its input. Other arguments show nothing,
/// as in the web app.
pub fn command_of(input: &str) -> Option<String> {
    let input: serde_json::Value = serde_json::from_str(input).ok()?;
    let command = ["command", "cmd"]
        .into_iter()
        .find_map(|key| input.get(key))?;
    if let Some(text) = command.as_str() {
        return Some(text.to_string());
    }
    // Codex sends a command as its argv, as ToolSummary.cs reads it.
    let argv = command.as_array()?.iter().map(serde_json::Value::as_str);
    argv.collect::<Option<Vec<_>>>()
        .map(|parts| parts.join(" "))
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
        let (success, danger) = (theme.success, theme.danger);
        let state = match self.tool.state {
            ToolState::Started if self.took_ms.is_some() => StepState::Running,
            ToolState::Started => StepState::Unfinished,
            ToolState::Completed => StepState::Done,
            ToolState::Failed => StepState::Failed,
        };
        let open = self.kept.open && self.tool.has_detail;
        let turned = turn(open, self.kept.grown_at(), window, cx);
        let faded = faded(turned.0);
        let (key, on_action) = (self.key.clone(), self.on_action.clone());
        let on_click = self.tool.has_detail.then(|| {
            Box::new(move |_: &ClickEvent, window: &mut Window, cx: &mut App| {
                on_action(SessionAction::Toggle { key: key.clone() }, window, cx)
            }) as OnClick
        });
        let tags = [
            self.tool
                .added
                .filter(|n| *n > 0)
                .map(|added| div().text_color(success).child(format!("+{added}"))),
            self.tool
                .removed
                .filter(|n| *n > 0)
                .map(|removed| div().text_color(danger).child(format!("-{removed}"))),
        ]
        .into_iter()
        .flatten()
        .map(IntoElement::into_any_element)
        .collect();
        let line = StepRow {
            id: SharedString::from(format!("{}:line", self.key)),
            state,
            title: Fold::tool_title(&self.tool.name).into(),
            summary: self.tool.summary.clone().map(Into::into),
            tags,
            took_ms: self.took_ms,
            chevron: self
                .tool
                .has_detail
                .then(|| turn(open, self.kept.toggled_at, window, cx).0),
            on_click,
        };
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
        // Lit as its body grows: not while an opened call's first detail is still on its way.
        let lit = if self.kept.detail.is_some() {
            turned.0
        } else {
            0.
        };
        band(column, lit, cx)
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
        if let Some(command) = detail.input.as_deref().and_then(command_of) {
            column = column.child(block().child(command));
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

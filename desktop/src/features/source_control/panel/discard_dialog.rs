//! Discard always asks first and names what goes. Everything keeps new files unless they're ticked; a new file is
//! deleted from disk, so it says so; Cancel sends nothing.
use super::git_action::GitAction;
use super::git_status::ChangedFile;
use super::git_target::GitTarget;
use gpui_kit::assets::IconName;
use gpui_kit::component::button::ButtonVariant;
use gpui_kit::component::checkbox::Checkbox;
use gpui_kit::component::{ActiveTheme as _, Icon, WindowExt as _, v_flex};
use gpui_kit::*;
use serde_json::{Value, json};
use std::cell::Cell;
use std::rc::Rc;

/// The new files `Git` would delete from disk rather than restore.
fn deletes_from_disk(file: &ChangedFile) -> bool {
    file.index == "?" || file.index == "A"
}
fn count(number: usize) -> String {
    if number == 1 {
        "1 file".into()
    } else {
        format!("{number} files")
    }
}
#[derive(Clone, Debug, PartialEq)]
pub enum DiscardRequest {
    Everything {
        fingerprint: String,
        listed: Vec<ChangedFile>,
        omitted: usize,
    },
    Files(Vec<ChangedFile>),
}
#[derive(Clone, Debug, PartialEq)]
pub struct DiscardQuestion {
    pub title: String,
    pub intro: &'static str,
    pub lines: Vec<String>,
    pub confirm: &'static str,
}
impl DiscardRequest {
    fn files(&self) -> &[ChangedFile] {
        match self {
            Self::Everything { listed, .. } => listed,
            Self::Files(files) => files,
        }
    }
    pub fn question(&self, include_new: bool) -> DiscardQuestion {
        match self {
            Self::Everything { .. } => DiscardQuestion {
                title: "Discard every change?".into(),
                intro: "This throws away the whole working copy, not just what is on screen.",
                lines: vec![
                    "Every tracked file goes back to how the last commit has it, staged or not."
                        .into(),
                    "Git keeps no copy of this. It cannot be undone.".into(),
                ],
                confirm: if include_new {
                    "Discard and delete"
                } else {
                    "Discard everything"
                },
            },
            Self::Files(files) => {
                let deleted = files.iter().filter(|file| deletes_from_disk(file)).count();
                let restored = files.len() - deleted;
                let mut lines = Vec::new();
                if deleted > 0 {
                    lines.push(format!(
                        "{} git has never tracked will be deleted from disk.",
                        count(deleted)
                    ));
                }
                if restored > 0 {
                    lines.push(format!(
                        "{} goes back to how the last commit has it, staged or not.",
                        count(restored)
                    ));
                }
                DiscardQuestion {
                    title: if files.len() == 1 {
                        "Discard changes to this file?".into()
                    } else {
                        format!("Discard changes to {} files?", files.len())
                    },
                    intro: "The work in it is not committed anywhere, so it goes for good.",
                    lines,
                    confirm: if restored == 0 {
                        "Delete the file"
                    } else {
                        "Discard changes"
                    },
                }
            }
        }
    }
    /// A rename restores both paths: the new name goes and the old one comes back.
    pub fn args(&self, session: &str, include_new: bool) -> Value {
        match self {
            Self::Everything { fingerprint, .. } => {
                json!({"session":session,"expectedFingerprint":fingerprint,"includeUntracked":include_new})
            }
            Self::Files(files) => {
                let tracked: Vec<&str> = files
                    .iter()
                    .filter(|file| file.index != "?")
                    .flat_map(|file| std::iter::once(file.path.as_str()).chain(file.original_path.as_deref()))
                    .collect();
                let untracked: Vec<&str> = files
                    .iter()
                    .filter(|file| file.index == "?")
                    .map(|file| file.path.as_str())
                    .collect();
                json!({"session":session,"tracked":tracked,"untracked":untracked,"expected":files})
            }
        }
    }
}
pub fn ask(
    request: DiscardRequest,
    target: GitTarget,
    action: Entity<GitAction>,
    window: &mut Window,
    cx: &mut App,
) {
    let include_new = Rc::new(Cell::new(false));
    window.open_alert_dialog(cx, move |alert, window, cx| {
        let danger = cx.theme().danger;
        let muted = cx.theme().muted_foreground;
        let question = request.question(include_new.get());
        let deleted: Vec<&ChangedFile> = request
            .files()
            .iter()
            .filter(|file| deletes_from_disk(file))
            .collect();
        let kept: Vec<&ChangedFile> = request
            .files()
            .iter()
            .filter(|file| !deletes_from_disk(file))
            .collect();
        let name = |file: &ChangedFile| div().text_sm().truncate().child(file.label());
        let mut body = v_flex()
            .gap_2()
            .child(div().text_sm().child(question.intro))
            .children(
                question
                    .lines
                    .iter()
                    .map(|line| div().text_sm().text_color(muted).child(line.clone())),
            );
        let mut names = v_flex().id("discard-files").max_h(px(240.)).overflow_y_scroll();
        if !deleted.is_empty() {
            names = names
                .child(
                    div()
                        .text_sm()
                        .text_color(danger)
                        .child("These new files will be deleted"),
                )
                .children(deleted.iter().map(|file| name(file)));
        }
        names = names.children(kept.iter().map(|file| name(file)));
        body = body.child(names);
        if let DiscardRequest::Everything { omitted, .. } = &request {
            if *omitted > 0 {
                body = body.child(div().text_sm().child(format!("and {omitted} more")));
            }
            let toggled = include_new.clone();
            let redraw = window.window_handle();
            body = body.child(
                Checkbox::new("discard-include-new")
                    .label("Also delete new files")
                    .checked(include_new.get())
                    .on_click(move |value, _, cx| {
                        toggled.set(*value);
                        _ = redraw.update(cx, |_, window, _| window.refresh());
                    }),
            );
        }
        let chosen = include_new.clone();
        let request = request.clone();
        let target = target.clone();
        let action = action.clone();
        alert
            .icon(Icon::new(IconName::TriangleAlert).text_color(danger))
            .title(question.title)
            .description(body)
            .ok_text(question.confirm)
            .ok_variant(ButtonVariant::Danger)
            .cancel_text(if matches!(request, DiscardRequest::Everything { .. }) {
                "Keep my changes"
            } else {
                "Cancel"
            })
            .show_cancel(true)
            .on_ok(move |_, _, cx| {
                let args = request.args(&target.session, chosen.get());
                action.update(cx, |action, cx| action.run(&target, "discard", args, cx));
                true
            })
    });
}
#[cfg(test)]
#[path = "discard_dialog_tests.rs"]
mod tests;

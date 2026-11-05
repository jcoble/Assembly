//! The Context tab's Files touched card: each file the session's calls and prompts named, newest first, by name with
//! its folder muted, a count chip when more than one call or prompt named it, and its lines added and removed. A click
//! opens a file through the normal open (`OpenFile`), inside the session's folder or outside it alike, and an attached
//! image full size, as the Attachments card does.

use gpui_kit::assets::IconName;
use gpui_kit::component::{ActiveTheme as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::listing::Listing;
use super::{ContextPane, FileTouched};
use crate::activity::{StatusChip, Tone};
use crate::core::intents::open_file::OpenFile;
use crate::features::session::client::session_client::CallError;
use crate::section_card::SectionCard;

impl ContextPane {
    pub(super) fn files_card(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let theme = cx.theme();
        let (muted, success, danger, hover) = (
            theme.muted_foreground,
            theme.success,
            theme.danger,
            theme.muted,
        );
        let note = |id: &'static str, text: SharedString| {
            div()
                .debug_selector(move || id.into())
                .px_2()
                .text_color(muted)
                .child(text)
        };
        let body = match &self.files {
            Listing::Reading => note("context-note-reading", "Reading this session…".into()),
            Listing::Failed(why) => note(
                "context-note-failed",
                format!("Couldn't read files: {why}").into(),
            ),
            Listing::Read(files) if files.is_empty() => note(
                "context-note-empty",
                "No files yet. Files this session reads or edits are listed here, newest first."
                    .into(),
            ),
            Listing::Read(files) => {
                v_flex().children(files.iter().zip(row_looks(files, cx)).enumerate().map(
                    |(index, (file, look))| {
                        let selector = format!("touched-file-{}", file.path);
                        let chip = format!("touched-count-{}", file.path);
                        let clicked = file.clone();
                        h_flex()
                            .id(("touched-file", index))
                            .debug_selector(move || selector)
                            .h(px(28.))
                            .px_2()
                            .gap_2()
                            .cursor_pointer()
                            .hover(|row| row.bg(hover))
                            .on_click(cx.listener(move |pane, _, window, cx| {
                                pane.open_touched(&clicked, window, cx)
                            }))
                            .child(
                                div()
                                    .min_w_0()
                                    .truncate()
                                    .text_color(look.name_color)
                                    .child(look.name),
                            )
                            .child(
                                div()
                                    .flex_1()
                                    .min_w_0()
                                    .truncate()
                                    // A path's end says more than its start.
                                    .text_ellipsis_start()
                                    .text_color(look.folder_color)
                                    .child(look.folder),
                            )
                            .when(file.count > 1, |row| {
                                row.child(
                                    div().flex_none().debug_selector(move || chip).child(
                                        StatusChip::new(file.count.to_string(), Tone::Neutral),
                                    ),
                                )
                            })
                            .when(file.added > 0, |row| {
                                row.child(
                                    div()
                                        .flex_none()
                                        .text_color(success)
                                        .child(format!("+{}", file.added)),
                                )
                            })
                            .when(file.removed > 0, |row| {
                                row.child(
                                    div()
                                        .flex_none()
                                        .text_color(danger)
                                        .child(format!("-{}", file.removed)),
                                )
                            })
                    },
                ))
            }
        };
        let count = match &self.files {
            Listing::Read(files) => files.len(),
            _ => 0,
        };
        SectionCard::new(IconName::Files, "Files touched")
            .count(count)
            .child(body)
    }

    /// An attached image opens full size, as the Attachments card does; any other file goes to the normal open with
    /// the session and the source it was listed from. Nothing opens from a source that's been refused.
    fn open_touched(&mut self, file: &FileTouched, window: &mut Window, cx: &mut Context<Self>) {
        let Some(scope) = self.context_scope.as_ref().filter(|scope| scope.valid) else {
            return;
        };
        if let Some(name) = &file.name {
            // As the Attachments card: a remote session's image opens only through its captured source.
            if self.machine.is_none() || scope.source.is_some() {
                self.open(file.path.clone(), name.clone(), window, cx);
            }
            return;
        }
        window.dispatch_action(
            Box::new(OpenFile {
                session_id: scope.session.clone(),
                path: file.path.clone(),
                expected_source: scope.source.clone(),
                line: None,
                column: None,
                pinned: false,
            }),
            cx,
        );
    }
}

/// What a row says and how: its name (an attached image's name when it has one), its folder, and their colours. Every
/// row looks alike, a file outside the session's folder included.
#[derive(Clone, Debug, PartialEq)]
pub(super) struct RowLook {
    pub name: String,
    pub name_color: Hsla,
    pub folder: String,
    pub folder_color: Hsla,
}

pub(super) fn row_looks(files: &[FileTouched], cx: &App) -> Vec<RowLook> {
    let theme = cx.theme();
    files
        .iter()
        .zip(names(files))
        .map(|(file, (name, folder))| RowLook {
            name: file.name.clone().unwrap_or_else(|| name.to_string()),
            name_color: theme.foreground,
            folder: folder.to_string(),
            folder_color: theme.muted_foreground,
        })
        .collect()
}

/// `rows/files`' answer, or why it couldn't be read.
pub(super) fn touched(answer: Result<serde_json::Value, CallError>) -> Listing<FileTouched> {
    match answer.and_then(|list| {
        serde_json::from_value(list).map_err(|error| CallError::Failed(error.to_string()))
    }) {
        Ok(list) => Listing::Read(list),
        Err(error) => Listing::Failed(error.reason()),
    }
}

/// Each file's name and its folder below the folder all the files share, which is usually the session's checkout: an
/// agent sends whole paths. A lone file shows no folder.
pub(super) fn names(files: &[FileTouched]) -> Vec<(&str, &str)> {
    fn folder(path: &str) -> &str {
        path.rsplit_once('/').map_or("", |(folder, _)| folder)
    }
    let mut shared = files.first().map_or("", |file| folder(&file.path));
    for file in files {
        while !shared.is_empty()
            && !file
                .path
                .strip_prefix(shared)
                .is_some_and(|rest| rest.starts_with('/'))
        {
            shared = folder(shared);
        }
    }
    files
        .iter()
        .map(|file| {
            // With no folder shared, a whole path keeps its leading `/` or `~/`, so it doesn't read as relative.
            let rest = match shared {
                "" => file.path.as_str(),
                _ => &file.path[shared.len() + 1..],
            };
            let (folder, name) = rest.rsplit_once('/').unwrap_or(("", rest));
            (name, folder)
        })
        .collect()
}

#[cfg(test)]
#[path = "files_touched_tests.rs"]
mod tests;

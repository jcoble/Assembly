//! The Context tab's Files touched card: each file the session's calls and prompts named, newest first, by name with
//! its folder muted, a count chip when more than one call or prompt named it, and its lines added and removed. A click
//! opens a file inside the session's folder through the normal open (`OpenFile`) and an attached image full size, as
//! the Attachments card does. A file outside the folder looks the same, but its click is held until TSK-1388 lands
//! (`held_until_outside_opener`), and meanwhile it keeps the normal cursor rather than the pointer.

use std::path::{Component, Path};

use gpui_kit::assets::IconName;
use gpui_kit::component::list::ListItem;
use gpui_kit::component::tooltip::Tooltip;
use gpui_kit::component::{ActiveTheme as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::listing::Listing;
use super::{ContextPane, FileTouched};
use crate::activity::{StatusChip, Tone};
use crate::core::intents::open_file::OpenFile;
use crate::core::state::preferences::Preferences;
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::features::session::client::session_client::CallError;
use crate::section_card::SectionCard;

impl ContextPane {
    pub(super) fn files_card(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let theme = cx.theme();
        let (muted, success, danger) = (theme.muted_foreground, theme.success, theme.danger);
        // Every row is one Body line tall: a count chip is taller than the line and would otherwise grow its row.
        let line_height = Preferences::read(cx, |appearance| appearance.ui_text(TextRole::Body)).1;
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
                        let held = held_until_outside_opener(file);
                        let line =
                            h_flex()
                                .w_full()
                                .min_w_0()
                                .h(line_height)
                                .gap_2()
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
                                    row.child(div().flex_none().debug_selector(move || chip).child(
                                        StatusChip::new(file.count.to_string(), Tone::Neutral),
                                    ))
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
                                });
                        ListItem::new(("touched-file", index))
                            .debug_selector(move || selector)
                            .text_role(TextRole::Body, cx)
                            .cursor(row_cursor(file))
                            .when_some(held, |row, note| {
                                row.tooltip(move |window, cx| {
                                    Tooltip::new(note.clone()).build(window, cx)
                                })
                            })
                            .on_click(cx.listener(move |pane, _, window, cx| {
                                pane.open_touched(&clicked, window, cx)
                            }))
                            .child(line)
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
        if held_until_outside_opener(file).is_some() {
            return;
        }
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
                outside: None,
            }),
            cx,
        );
    }
}

impl ContextPane {
    /// Reads the files again in their own task, as a turn ending while shown does, while the source is valid.
    pub(super) fn reread_files(&mut self, cx: &mut Context<Self>) {
        let Some(scope) = self.context_scope.clone().filter(|scope| scope.valid) else {
            return;
        };
        self.files_serial += 1;
        let (serial, files_serial) = (self.read_serial, self.files_serial);
        let answer = (self.client).call("rows", "files", scope.args(), Some("rows/files"), cx);
        self._files = Some(cx.spawn(async move |pane, cx| {
            let answer = answer.await;
            pane.update(cx, |pane, cx| {
                if !pane.current(serial, &scope) {
                    return;
                }
                if answer.as_ref().is_err_and(CallError::is_source_changed) {
                    pane.refuse_source(&scope, super::SOURCE_CHANGED.into(), cx);
                }
                pane.take_files(files_serial, answer, true);
                cx.notify();
            })
            .ok();
        }));
    }

    /// Keeps the answer of files read `serial`, or why it couldn't be read, unless the list already holds a newer
    /// read's answer. A failed re-read (`keep_on_failure`) leaves the list as it is.
    pub(super) fn take_files(
        &mut self,
        serial: u64,
        answer: Result<serde_json::Value, CallError>,
        keep_on_failure: bool,
    ) {
        if serial < self.files_shown || (keep_on_failure && answer.is_err()) {
            return;
        }
        self.files_shown = serial;
        self.files = touched(answer);
    }
}

/// TSK-1388 hold: the normal open can't save a file outside the session's folder, so until the outside-file opener
/// lands such a row's click does nothing and its hover gives this note. A row is outside when it isn't an attached image
/// and its path is whole (absolute or `~/`) or climbs out (`..`); `rows/files` sends a path inside the folder relative.
/// When the opener lands, delete this function, `row_cursor` and their uses.
pub(super) fn held_until_outside_opener(file: &FileTouched) -> Option<String> {
    let path = Path::new(&file.path);
    let outside = file.name.is_none()
        && (path.is_absolute()
            || file.path.starts_with("~/")
            || path.components().any(|part| {
                matches!(
                    part,
                    Component::ParentDir | Component::RootDir | Component::Prefix(_)
                )
            }));
    outside.then(|| {
        format!(
            "{} — opens once files outside the session's folder can be opened",
            file.path
        )
    })
}

/// The pointer on a row that opens; a held row's click does nothing, so it keeps the normal cursor.
pub(super) fn row_cursor(file: &FileTouched) -> CursorStyle {
    match held_until_outside_opener(file) {
        Some(_) => CursorStyle::Arrow,
        None => CursorStyle::PointingHand,
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

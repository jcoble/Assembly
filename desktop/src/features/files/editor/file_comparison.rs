//! A comparison retains its exact observed disk revision and draft version for an explicit overwrite.
use crate::core::ui::diff::parse_patch;
use crate::features::changes::file_diff::FileDiff;
use gpui_kit::component::diff::{Diff, DiffState};
use gpui_kit::component::{ActiveTheme as _, v_flex};
use gpui_kit::*;
use serde::Deserialize;

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub(super) struct ComparedFile {
    pub disk_revision: Option<String>,
    pub diff: FileDiff,
}

pub(super) struct Comparison {
    version: u64,
    read: ComparedFile,
}
impl Comparison {
    pub fn new(version: u64, read: ComparedFile) -> Self {
        Self { version, read }
    }
    pub fn overwrite_revision(&self, version: u64) -> Result<&str, &'static str> {
        if version != self.version {
            return Err("The draft changed after comparison. Compare it again before overwriting.");
        }
        self.read
            .disk_revision
            .as_deref()
            .ok_or("The disk file is missing. Overwrite cannot recreate it.")
    }
}

pub(super) struct FileComparison {
    pub comparison: Comparison,
    diff: Option<Entity<DiffState>>,
    error: Option<String>,
}
impl FileComparison {
    pub fn new(version: u64, read: ComparedFile, cx: &mut Context<Self>) -> Self {
        let (diff, error) = if read.diff.binary || read.diff.unstaged.is_empty() {
            (None, None)
        } else {
            match parse_patch(&read.diff.unstaged, read.diff.truncated) {
                Ok(files) => (Some(cx.new(|cx| DiffState::new(files, cx))), None),
                Err(error) => (None, Some(error)),
            }
        };
        Self {
            comparison: Comparison::new(version, read),
            diff,
            error,
        }
    }
}
impl Render for FileComparison {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        v_flex()
            .size_full()
            .text_color(cx.theme().muted_foreground)
            .child("Disk → unsaved draft")
            .children(
                self.comparison
                    .read
                    .diff
                    .notes()
                    .into_iter()
                    .map(|note| div().px_3().child(note)),
            )
            .children(self.error.clone().map(|error| div().px_3().child(error)))
            .children(self.diff.as_ref().map(|state| Diff::new(state)))
    }
}
#[cfg(test)]
#[path = "file_comparison_tests.rs"]
mod tests;

//! Which file the history card shows: whichever changed last of the tree's selection and the file on screen in the
//! editor. Each falls back to the other when it clears, as in the old panel.

#[cfg(test)]
#[path = "history_target_tests.rs"]
mod tests;

#[derive(Clone, Debug, Default, PartialEq)]
pub enum Picked {
    #[default]
    Nothing,
    /// A folder, which has no file history.
    Folder,
    /// A file in another session or source, whose history isn't this tree's to show.
    Elsewhere,
    File(String),
}

#[derive(Default)]
pub struct HistoryTarget {
    selected: Picked,
    editor: Picked,
    editor_last: bool,
}

impl HistoryTarget {
    /// The tree's selection. The same pick again changes nothing, so it doesn't take over from the editor.
    pub fn selected(&mut self, picked: Picked) {
        if self.selected != picked {
            self.selected = picked;
            self.editor_last = false;
        }
    }

    /// The file on screen in the editor. The same file again changes nothing.
    pub fn editor(&mut self, picked: Picked) {
        if self.editor != picked {
            self.editor = picked;
            self.editor_last = true;
        }
    }

    pub fn current(&self) -> Option<&str> {
        let (last, other) = if self.editor_last {
            (&self.editor, &self.selected)
        } else {
            (&self.selected, &self.editor)
        };
        match (last, other) {
            (Picked::File(path), _) | (Picked::Nothing, Picked::File(path)) => Some(path),
            _ => None,
        }
    }
}

//! `FileText` is an open file's text as its parts come in (`files/read`): its lines, and what to ask for next. A part
//! ends at a line end unless one line is longer than a part, so a part that doesn't end in a newline carries on the last
//! line. A part from a changed file starts the text over from the top, and so does the first failed later part (the file
//! may have shrunk); a second failure is shown.

use super::entry::FileChunk;

#[cfg(test)]
#[path = "file_text_tests.rs"]
mod tests;

/// How close to the last line held the view may scroll before the next part is asked for.
pub const NEAR_END: usize = 200;

#[derive(Default)]
pub struct FileText {
    pub lines: Vec<String>,
    /// The longest line's index, which sets how far the view scrolls sideways.
    pub widest: usize,
    pub line_count: usize,
    pub binary: bool,
    pub error: Option<String>,
    revision: Option<String>,
    /// Where the next part starts, while there is one.
    next: Option<u64>,
    /// The part being loaded.
    loading: Option<u64>,
    /// The last part didn't end in a newline.
    open_line: bool,
    /// A failed part has already started the text over once.
    restarted: bool,
}

impl FileText {
    /// Starts reading from the top; returns the offset to ask for.
    pub fn start(&mut self) -> u64 {
        *self = Self::default();
        self.loading = Some(0);
        0
    }

    pub fn is_loading(&self) -> bool {
        self.loading.is_some()
    }

    /// The next part's offset, once `last_shown` is near the end and nothing is loading; it's then loading.
    pub fn more(&mut self, last_shown: usize) -> Option<u64> {
        let next = self
            .next
            .filter(|_| self.loading.is_none() && last_shown + NEAR_END >= self.lines.len())?;
        self.loading = Some(next);
        Some(next)
    }

    /// Takes the answer for the part at `offset`; an answer for a part not being loaded is dropped. Returns an offset to
    /// ask for at once when the text starts over.
    pub fn loaded(&mut self, offset: u64, answer: Result<FileChunk, String>) -> Option<u64> {
        if self.loading != Some(offset) {
            return None;
        }
        self.loading = None;
        let chunk = match answer {
            Ok(chunk) => chunk,
            Err(_) if offset > 0 && !self.restarted => {
                self.start_over();
                self.restarted = true;
                return Some(0);
            }
            Err(error) => {
                self.error = Some(error);
                return None;
            }
        };
        if self
            .revision
            .as_ref()
            .is_some_and(|revision| *revision != chunk.revision)
        {
            return Some(self.start_over());
        }
        self.revision = Some(chunk.revision);
        self.line_count = chunk.line_count;
        self.binary = chunk.binary;
        self.next = chunk.next_offset;
        if chunk.content.is_empty() {
            return None;
        }
        let ends_line = chunk.content.ends_with('\n');
        let body = chunk.content.strip_suffix('\n').unwrap_or(&chunk.content);
        for (ix, line) in body.split('\n').enumerate() {
            let line = line.strip_suffix('\r').unwrap_or(line);
            if ix == 0 && self.open_line {
                self.lines.last_mut().expect("an open line").push_str(line);
            } else {
                self.lines.push(line.to_string());
            }
            let last = self.lines.len() - 1;
            if self.lines[last].len() > self.lines[self.widest].len() {
                self.widest = last;
            }
        }
        self.open_line = !ends_line;
        None
    }

    fn start_over(&mut self) -> u64 {
        let restarted = self.restarted;
        self.start();
        self.restarted = restarted;
        0
    }
}

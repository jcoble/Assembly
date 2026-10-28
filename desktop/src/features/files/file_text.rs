//! `FileText` is an open file's text as its parts come in (`files/read`): its lines, and what to ask for next. A part
//! ends at a line end unless one line is longer than a part, so a part that doesn't end in a newline carries on the last
//! line. A part from a changed file starts the text over from the top, and so does a failed later part (the file may
//! have shrunk), once: a second start-over would chase a file that keeps changing, so it stops and says so.

use super::entry::FileChunk;

#[cfg(test)]
#[path = "file_text_tests.rs"]
mod tests;

/// What a file that changed twice while it was read says.
pub const KEEPS_CHANGING: &str =
    "This file keeps changing while it's read. Close its tab and open it again to read it fresh.";

/// How close to the last line held the view may scroll before the next part is asked for.
pub const NEAR_END: usize = 200;

#[derive(Default)]
pub struct FileText {
    pub lines: Vec<String>,
    /// The longest line's index, which sets how far the view scrolls sideways.
    pub widest: usize,
    /// The longest line's length in characters, not bytes: two-byte "é" is no wider than "a" in the code font.
    widest_chars: usize,
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
    /// A changed file or a failed part has already started the text over once.
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
                self.failed(error);
                return None;
            }
        };
        if self
            .revision
            .as_ref()
            .is_some_and(|revision| *revision != chunk.revision)
        {
            if self.restarted {
                self.failed(KEEPS_CHANGING.into());
                return None;
            }
            self.start_over();
            self.restarted = true;
            return Some(0);
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
        let last_part = body.split('\n').count() - 1;
        for (ix, part) in body.split('\n').enumerate() {
            if ix == 0 && self.open_line {
                self.lines.last_mut().expect("an open line").push_str(part);
            } else {
                self.lines.push(part.to_string());
            }
            let last = self.lines.len() - 1;
            // A line ended by "\r\n" drops the "\r", once its "\n" has come.
            if (ix < last_part || ends_line) && self.lines[last].ends_with('\r') {
                self.lines[last].pop();
            }
            let chars = self.lines[last].chars().count();
            if chars > self.widest_chars {
                self.widest = last;
                self.widest_chars = chars;
            }
        }
        self.open_line = !ends_line;
        None
    }

    /// Shows `error` and asks for nothing more.
    pub fn failed(&mut self, error: String) {
        self.error = Some(error);
        self.loading = None;
        self.next = None;
    }

    fn start_over(&mut self) -> u64 {
        let restarted = self.restarted;
        self.start();
        self.restarted = restarted;
        0
    }
}

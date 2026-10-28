//! `Output` owns which of a terminal's bytes the screen has, by byte offset, so each byte is drawn once and in order
//! (terminal#3, terminal#5). The `terminal` topic's rows and `terminal/read`'s pages both carry offsets: a row or page
//! is cut to what's past the last byte applied. A jump (a new view, or one too far behind) reads the replay page by
//! page from at most 4 MB before the jump, and holds the live rows that come meanwhile until it catches up.
//! Replaces WorkspaceTerminal.svelte's replay queue (lines 81-131).

use base64::Engine as _;
use base64::engine::general_purpose::STANDARD;
use serde::Deserialize;

/// How far back a new view reads (WorkspaceTerminal.svelte's 4 MB).
pub const REPLAY: i64 = 4 * 1024 * 1024;

/// One row of the `terminal` topic (dotnet/Assembly.Application/Features/Terminal/OutputRow.cs): the bytes from
/// `prev_seq` to `seq`, or, as the last row, the exit with `seq` one past the end.
#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OutputRow {
    pub seq: i64,
    pub prev_seq: i64,
    pub data: Option<String>,
    pub exit: Option<i32>,
}

/// `terminal/read`'s answer: `data` starts at `offset`.
#[derive(Clone, Debug, Deserialize)]
pub struct OutputPiece {
    pub offset: i64,
    pub data: String,
}

#[derive(Default)]
pub struct Output {
    /// The offset after the last byte the screen has.
    applied: i64,
    /// While a replay is read: the offset it reads up to, where the live rows continue.
    replay_to: Option<i64>,
    held: Vec<OutputRow>,
    exit: Option<i32>,
}

impl Output {
    pub fn exit(&self) -> Option<i32> {
        self.exit
    }

    /// The rows before `seq` weren't sent: the offset to read the replay from, or `None` when the screen already has
    /// them.
    pub fn jump(&mut self, seq: i64) -> Option<i64> {
        if self.applied >= seq {
            self.replay_to = None;
            return None;
        }
        self.replay_to = Some(seq);
        Some(self.applied.max(seq - REPLAY))
    }

    /// A live row: its new bytes for the screen, or nothing while a replay is still being read.
    pub fn row(&mut self, row: OutputRow) -> Vec<u8> {
        if self.replay_to.is_some() {
            self.held.push(row);
            return Vec::new();
        }
        self.apply(row)
    }

    /// A replay page: its new bytes, then the held rows' once the replay reaches its end, and the offset of the next
    /// page while there's more to read.
    pub fn page(&mut self, piece: OutputPiece) -> (Vec<u8>, Option<i64>) {
        let Some(to) = self.replay_to else {
            return (Vec::new(), None);
        };
        let mut bytes = decode(&piece.data);
        bytes.truncate((to - piece.offset).clamp(0, bytes.len() as i64) as usize);
        let end = piece.offset + bytes.len() as i64;
        let mut out = self.take(piece.offset, end, bytes);
        // Nothing more to read: the replay reached its end, or the ring has nothing past it (an empty page).
        if self.applied < to && end > piece.offset {
            return (out, Some(self.applied));
        }
        self.replay_to = None;
        for row in std::mem::take(&mut self.held) {
            out.extend(self.apply(row));
        }
        (out, None)
    }

    fn apply(&mut self, row: OutputRow) -> Vec<u8> {
        if let Some(code) = row.exit {
            self.exit = Some(code);
            return Vec::new();
        }
        let bytes = row.data.as_deref().map(decode).unwrap_or_default();
        self.take(row.prev_seq, row.seq, bytes)
    }

    /// The part of `bytes` (from `start` to `end`) past what's applied. A start past it means the backend trimmed
    /// what was between; the screen carries on from what it has.
    fn take(&mut self, start: i64, end: i64, mut bytes: Vec<u8>) -> Vec<u8> {
        if end <= self.applied {
            return Vec::new();
        }
        let skip = (self.applied - start).clamp(0, bytes.len() as i64) as usize;
        self.applied = end;
        bytes.drain(..skip);
        bytes
    }
}

fn decode(data: &str) -> Vec<u8> {
    STANDARD.decode(data).unwrap_or_else(|error| {
        eprintln!("terminal: output that isn't base64: {error}");
        Vec::new()
    })
}

#[cfg(test)]
#[path = "output_tests.rs"]
mod tests;

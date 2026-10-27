//! `Meter` owns the running meter: GPUI's frame trace, the key times not yet on screen, and the CSV file. Every fifth
//! `second` tick of the backend's clock it reads the frames the trace saw since the last line and appends one
//! `MeterLine` to the CSV and to stdout.

use std::path::PathBuf;
use std::time::Instant;

use gpui_kit::*;

use super::meter_line::MeterLine;

pub struct Meter {
    csv: PathBuf,
    collector: FrameTimingCollector,
    /// A draw whose present hasn't been read yet; it may come in the next batch.
    last_draw: Option<FrameTiming>,
    /// Keys that reached the window and aren't on screen yet.
    waiting_keys: Vec<Instant>,
    /// The worst lag and key-to-screen so far.
    worst: MeterLine,
    ticks: u32,
}

impl Global for Meter {}

impl Meter {
    /// Turns GPUI's frame trace on, watches keys, and subscribes to the clock's `second` tick. It's kept as a global.
    pub fn start(_csv: PathBuf, _cx: &mut App) {
        todo!()
    }

    /// One `second` tick: every fifth one writes a line.
    fn on_second(&mut self) {
        todo!()
    }
}

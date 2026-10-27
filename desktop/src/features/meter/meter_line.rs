//! `MeterLine` owns one interval's numbers and its CSV line, worked out from frame timings and key times alone, so it
//! is unit tested from a recorded trace. Columns: frames, lag p50 and max (first change that dirtied a frame to that
//! frame on screen), frames over 50 and 100 ms, longest draw, keys with key-to-screen p50 and max, longest stall and
//! stalls over 50 ms, the worst lag and key-to-screen so far, and the footprint. A stall here is a dirtied frame
//! waiting for the main thread before it started drawing; the spike measured how late a 100 ms tick ran, which the
//! guard no longer allows, so these two columns don't compare with the spike's.

use std::time::Instant;

use gpui_kit::*;

#[derive(Default)]
pub struct MeterLine {
    pub frames: usize,
    pub lag_p50_ms: f64,
    pub lag_max_ms: f64,
    pub lag_50: usize,
    pub lag_100: usize,
    pub draw_max_ms: f64,
    pub keys: usize,
    pub key_p50_ms: f64,
    pub key_max_ms: f64,
    pub stall_max_ms: f64,
    pub stalls_50: usize,
}

impl MeterLine {
    /// Works out the numbers from the frames seen since the last line, starting from `last_draw` (a draw from the last
    /// batch whose present is in this one) and leaving there a draw still waiting for its present. Takes out of
    /// `waiting_keys` the keys those frames put on screen.
    pub fn from_frames(
        _frames: &[FrameEvent],
        _last_draw: &mut Option<FrameTiming>,
        _waiting_keys: &mut Vec<Instant>,
    ) -> Self {
        todo!()
    }

    pub const HEADER: &str = "at,frames,lag_p50_ms,lag_max_ms,lag_50,lag_100,draw_max_ms,keys,key_p50_ms,key_max_ms,stall_max_ms,stalls_50,run_lag_max_ms,run_key_max_ms,footprint_mb";

    /// The CSV line, `at` seconds after the meter started, with the worst numbers so far and the footprint.
    pub fn csv(&self, _at_secs: f64, _worst: &MeterLine, _footprint_mb: f64) -> String {
        todo!()
    }
}

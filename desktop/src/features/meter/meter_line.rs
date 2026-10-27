//! `MeterLine` owns one interval's numbers and its CSV line, worked out from the frame trace, key times and stalls
//! alone, so it's unit tested from a made-up trace. Columns:
//!
//! - `frames`: frames that reached the screen.
//! - `lag_p50_ms`, `lag_max_ms`: from the first change that dirtied a frame to that frame on screen.
//! - `lag_50`, `lag_100`: frames whose lag passed 50 or 100 ms.
//! - `draw_max_ms`: the longest time GPUI spent drawing one frame.
//! - `keys`, `key_p50_ms`, `key_max_ms`: from a key reaching the window to the frame that shows it on screen.
//! - `stall_max_ms`, `stalls_50`: how late a 100 ms tick on the main thread ran, which is how long the main thread
//!   was busy with something else. Stalls over 50 ms are counted.
//! - `run_lag_max_ms`, `run_key_max_ms`: the worst so far.
//! - `footprint_mb`: the app's memory (footprint.rs).

use std::time::{Duration, Instant};

use gpui_kit::{FrameEvent, FrameTiming};

#[derive(Default)]
pub struct MeterLine {
    lags_ms: Vec<f64>,
    draws_ms: Vec<f64>,
    keys_ms: Vec<f64>,
    stalls_ms: Vec<f64>,
}

impl MeterLine {
    pub const HEADER: &str = "at,frames,lag_p50_ms,lag_max_ms,lag_50,lag_100,draw_max_ms,keys,key_p50_ms,key_max_ms,stall_max_ms,stalls_50,run_lag_max_ms,run_key_max_ms,footprint_mb";

    /// Works out the numbers from the frames seen since the last line. `last_draw` carries a draw whose present comes
    /// in a later batch; the keys those frames put on screen are taken out of `waiting_keys`.
    pub fn read(
        frames: Vec<FrameEvent>,
        last_draw: &mut Option<FrameTiming>,
        waiting_keys: &mut Vec<Instant>,
        stalls: &[Duration],
    ) -> Self {
        let mut line = Self {
            stalls_ms: stalls.iter().map(|stall| ms(*stall)).collect(),
            ..Self::default()
        };
        for event in frames {
            match event {
                FrameEvent::Draw(draw) => *last_draw = Some(draw),
                FrameEvent::Present(present) => {
                    let Some(draw) = last_draw.take() else {
                        continue;
                    };
                    let since = draw.dirty_at.unwrap_or(draw.draw_start);
                    line.lags_ms
                        .push(ms(present.present_end.saturating_duration_since(since)));
                    line.draws_ms.push(ms(draw.draw_end - draw.draw_start));
                    // Keys that reached the window before this frame started drawing are on screen now.
                    waiting_keys.retain(|key| {
                        let shown = *key <= draw.draw_start;
                        if shown {
                            line.keys_ms
                                .push(ms(present.present_end.saturating_duration_since(*key)));
                        }
                        !shown
                    });
                }
            }
        }
        line
    }

    pub fn lag_max_ms(&self) -> f64 {
        max(&self.lags_ms)
    }

    pub fn key_max_ms(&self) -> f64 {
        max(&self.keys_ms)
    }

    /// The CSV line, `at_secs` after the meter started, with the worst lag and key-to-screen so far.
    pub fn csv(
        &self,
        at_secs: f64,
        run_lag_max_ms: f64,
        run_key_max_ms: f64,
        footprint_mb: f64,
    ) -> String {
        format!(
            "{:.0},{},{:.1},{:.1},{},{},{:.1},{},{:.1},{:.1},{:.1},{},{:.1},{:.1},{:.1}",
            at_secs,
            self.lags_ms.len(),
            median(&self.lags_ms),
            max(&self.lags_ms),
            self.lags_ms.iter().filter(|lag| **lag > 50.0).count(),
            self.lags_ms.iter().filter(|lag| **lag > 100.0).count(),
            max(&self.draws_ms),
            self.keys_ms.len(),
            median(&self.keys_ms),
            max(&self.keys_ms),
            max(&self.stalls_ms),
            self.stalls_ms.iter().filter(|stall| **stall > 50.0).count(),
            run_lag_max_ms,
            run_key_max_ms,
            footprint_mb,
        )
    }
}

fn ms(duration: Duration) -> f64 {
    duration.as_secs_f64() * 1000.0
}

fn max(values: &[f64]) -> f64 {
    values.iter().copied().fold(0.0, f64::max)
}

fn median(values: &[f64]) -> f64 {
    let mut sorted = values.to_vec();
    sorted.sort_by(f64::total_cmp);
    sorted.get(sorted.len() / 2).copied().unwrap_or(0.0)
}

#[cfg(test)]
#[path = "meter_line_tests.rs"]
mod tests;

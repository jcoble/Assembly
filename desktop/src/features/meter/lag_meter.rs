//! `Meter` owns the running meter. It reads GPUI's own frame trace and never asks for a frame, so it doesn't cause
//! what it measures. Keys are timed as they reach the window, a 100 ms tick on the main thread times stalls, and
//! every 5 seconds a background task reads the trace and appends one `MeterLine` to the CSV and to stdout. All three
//! are held here, never detached.

use std::io::Write as _;
use std::path::PathBuf;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use gpui_kit::*;

use super::footprint::footprint_mb;
use super::meter_line::MeterLine;

const EVERY: Duration = Duration::from_secs(5);
const TICK: Duration = Duration::from_millis(100);

/// What the main thread saw since the last line.
#[derive(Default)]
struct Seen {
    keys: Vec<Instant>,
    stalls: Vec<Duration>,
}

pub struct Meter {
    _keys: Subscription,
    _stall_tick: Task<()>,
    _report: Task<()>,
}

impl Global for Meter {}

impl Meter {
    pub fn start(csv: PathBuf, cx: &mut App) {
        set_trace_enabled(true);
        let seen = Arc::new(Mutex::new(Seen::default()));

        let keys = seen.clone();
        let _keys =
            cx.intercept_keystrokes(move |_, _, _| keys.lock().unwrap().keys.push(Instant::now()));

        let stalls = seen.clone();
        let _stall_tick = cx.spawn(async move |cx| {
            loop {
                let asked = Instant::now();
                cx.background_executor().timer(TICK).await;
                stalls
                    .lock()
                    .unwrap()
                    .stalls
                    .push(asked.elapsed().saturating_sub(TICK));
            }
        });

        let executor = cx.background_executor().clone();
        let _report = cx.background_spawn(async move {
            let mut file = match std::fs::OpenOptions::new()
                .create(true)
                .append(true)
                .open(&csv)
            {
                Ok(file) => file,
                Err(error) => return eprintln!("meter: can't open {}: {error}", csv.display()),
            };
            _ = writeln!(file, "{}", MeterLine::HEADER);
            println!("meter: {}", MeterLine::HEADER);
            let started = Instant::now();
            let mut collector = FrameTimingCollector::new();
            let (mut last_draw, mut waiting_keys) = (None, Vec::new());
            let (mut run_lag, mut run_key) = (0.0f64, 0.0f64);
            loop {
                executor.timer(EVERY).await;
                let Seen { keys, stalls } = std::mem::take(&mut *seen.lock().unwrap());
                waiting_keys.extend(keys);
                let line = MeterLine::read(
                    collector.collect_unseen(),
                    &mut last_draw,
                    &mut waiting_keys,
                    &stalls,
                );
                run_lag = run_lag.max(line.lag_max_ms());
                run_key = run_key.max(line.key_max_ms());
                let csv_line = line.csv(
                    started.elapsed().as_secs_f64(),
                    run_lag,
                    run_key,
                    footprint_mb(),
                );
                _ = writeln!(file, "{csv_line}");
                println!("meter: {csv_line}");
            }
        });

        cx.set_global(Self {
            _keys,
            _stall_tick,
            _report,
        });
    }
}

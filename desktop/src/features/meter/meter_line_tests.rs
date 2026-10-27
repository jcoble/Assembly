use std::time::{Duration, Instant};

use gpui_kit::{DrawPhases, FrameEvent, FrameTiming, PresentTiming, WindowId};

use super::MeterLine;

fn at(start: Instant, ms: u64) -> Instant {
    start + Duration::from_millis(ms)
}

fn draw(start: Instant, dirty: u64, from: u64, to: u64) -> FrameEvent {
    FrameEvent::Draw(FrameTiming {
        window_id: WindowId::from(1),
        dirty_at: Some(at(start, dirty)),
        invalidations: 1,
        draw_start: at(start, from),
        draw_end: at(start, to),
        refresh_interval: None,
        signal_at: None,
        phases: DrawPhases::default(),
    })
}

fn present(start: Instant, end: u64) -> FrameEvent {
    FrameEvent::Present(PresentTiming {
        window_id: WindowId::from(1),
        present_start: at(start, end),
        present_end: at(start, end),
        animation_interval: None,
    })
}

// Pins: g0-host#14
#[test]
fn a_line_has_the_spike_columns_then_the_footprint() {
    let start = Instant::now();
    let mut last_draw = None;
    let mut keys = vec![at(start, 5), at(start, 300)];
    let frames = vec![
        draw(start, 0, 10, 30),
        present(start, 120),
        draw(start, 200, 210, 220),
        present(start, 240),
        // Drawn now, presented in the next batch.
        draw(start, 400, 410, 420),
    ];
    let stalls = [Duration::from_millis(10), Duration::from_millis(60)];
    let line = MeterLine::read(frames, &mut last_draw, &mut keys, &stalls);

    assert_eq!(MeterLine::HEADER.split(',').count(), 15);
    assert!(MeterLine::HEADER.ends_with(",footprint_mb"));
    assert_eq!(
        line.csv(5.0, line.lag_max_ms(), line.key_max_ms(), 210.0),
        "5,2,120.0,120.0,1,1,20.0,1,115.0,115.0,60.0,1,120.0,115.0,210.0"
    );
    assert_eq!(keys, [at(start, 300)]);

    let next = MeterLine::read(vec![present(start, 450)], &mut last_draw, &mut keys, &[]);
    assert_eq!(
        next.csv(10.0, 120.0, 140.0, 210.0),
        "10,1,50.0,50.0,0,0,10.0,1,150.0,150.0,0.0,0,120.0,140.0,210.0"
    );
    assert!(keys.is_empty());
}
